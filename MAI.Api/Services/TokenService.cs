using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MAI.Api.Security;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Security;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.IdentityModel.Tokens;

namespace MAI.Api.Services
{
    /// <summary>
    /// Implementarea <see cref="ITokenService"/>.
    ///
    /// Stă în MAI.Api, nu în MAI.BusinessLogic, pentru că depinde de stiva JWT a
    /// ASP.NET Core. Mutarea ei mai jos ar trage pachetul de tokenuri într-un
    /// strat care azi nu are nicio referință la ASP.NET.
    ///
    /// Singleton: nu ține stare per cerere și nu atinge baza de date. Cheia de
    /// semnare se materializează o singură dată, la construire.
    /// </summary>
    public sealed class TokenService : ITokenService
    {
        private readonly JwtOptions _jwt;
        private readonly TwoFactorOptions _twoFactor;
        private readonly SigningCredentials _credentials;

        public TokenService(JwtOptions jwt, TwoFactorOptions twoFactor)
        {
            _jwt       = jwt;
            _twoFactor = twoFactor;

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));
            _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        }

        public TokenIssueResult IssueTokens(User user, Guid sessionId)
        {
            var now            = DateTime.UtcNow;
            var accessExpires  = now.AddMinutes(_jwt.AccessTokenMinutes);
            var refreshExpires = now.AddDays(_jwt.RefreshTokenDays);

            var mfa          = CompletedSecondFactor(user);
            var accessToken  = GenerateJwtToken(user, sessionId, accessExpires, mfa);
            var refreshToken = GenerateOpaqueToken(64);

            var response = new TokenResponseDto
            {
                Id                    = user.Id,
                Username              = user.Username,
                FullName              = user.FullName ?? user.Username,
                Department            = user.OrgUnit?.Name ?? string.Empty,
                Role                  = user.Role,
                AccessToken           = accessToken,
                Token                 = accessToken,   // compatibilitate cu frontend-ul existent
                RefreshToken          = refreshToken,
                ExpiresIn             = _jwt.AccessTokenMinutes * 60,
                AccessTokenExpiresAt  = accessExpires,
                RefreshTokenExpiresAt = refreshExpires,
                MustChangePassword    = user.MustChangePassword,
                MfaEnrollmentRequired = _twoFactor.RequiredForPrivilegedRoles
                                        && user.Role >= UserRole.SefDirectie
                                        && !mfa,

                // Calculate din entitate, deci corecte și la /refresh: dacă
                // parola de domeniu se schimbă în timpul sesiunii, semnalul
                // apare la prima reîmprospătare, nu abia la următorul login.
                AuthProvider          = user.AuthProvider,
                KeyRewrapRequired     = user.KeyRewrapRequired,
            };

            return new TokenIssueResult(response, HashOpaqueToken(refreshToken), refreshExpires);
        }

        public (string Token, DateTime ExpiresAt) IssueTwoFactorChallenge(User user)
        {
            var token     = GenerateOpaqueToken(32);
            var expiresAt = DateTime.UtcNow.AddSeconds(_twoFactor.ChallengeLifetimeSeconds);

            // În baza de date ajunge doar hash-ul, ca și la refresh token: un dump
            // al bazei nu trebuie să conțină nimic care poate fi rejucat.
            user.TwoFactorChallengeHash      = HashOpaqueToken(token);
            user.TwoFactorChallengeExpiresAt = expiresAt;
            user.TwoFactorChallengeAttempts  = 0;

            return (token, expiresAt);
        }

        public void ClearChallenge(User user)
        {
            user.TwoFactorChallengeHash      = null;
            user.TwoFactorChallengeExpiresAt = null;
            user.TwoFactorChallengeAttempts  = 0;
        }

        public string HashOpaqueToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// True dacă sesiunea pentru acest cont se poate deschide DOAR prin pasul 2FA.
        ///
        /// Aceeași condiție ca în AuthController.Login: provocarea 2FA se emite
        /// când 2FA e activ ȘI secretul există. Înainte, claim-ul „amr” depindea
        /// doar de TwoFactorEnabled, deci un cont cu stare incoerentă (activat, dar
        /// fără secret) primea „mfa” fără să fi trecut prin al doilea factor.
        /// Acum claim-ul decide accesul la endpointurile privilegiate, deci
        /// trebuie să spună adevărul.
        ///
        /// La refresh condiția rămâne corectă: activarea 2FA închide toate
        /// sesiunile, iar dezactivarea lui face ca tokenurile următoare să
        /// primească „pwd”.
        /// </summary>
        private static bool CompletedSecondFactor(User user) =>
            user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret);

        private string GenerateJwtToken(User user, Guid sessionId, DateTime expires, bool mfa)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name,           user.Username),
                new(ClaimTypes.Role,           user.Role.ToString()),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),

                // Sesiunea din care provine tokenul. SessionTokenValidator o caută
                // la fiecare cerere: delogarea, închiderea sesiunii din profil,
                // dezactivarea contului sau schimbarea rolului invalidează tokenul
                // imediat, nu după cele până la 15 minute rămase până la expirare.
                new(SessionTokenValidator.SessionClaimType, sessionId.ToString()),

                // "amr" (authentication methods references), RFC 8176. Consemnează
                // cu ce a fost obținut tokenul. PrivilegedMfaFilter îl citește
                // când TwoFactor:RequiredForPrivilegedRoles este activ.
                new("amr", mfa ? "mfa" : "pwd"),
            };

            // Parola stabilită de administrator: PasswordChangeRequiredFilter
            // blochează cu acest token tot API-ul, în afara ecranului de
            // schimbare a parolei. Claim-ul lipsește complet când nu e nevoie,
            // nu apare cu o valoare „false” - filtrul caută doar prezența lui.
            if (user.MustChangePassword)
            {
                claims.Add(new Claim(
                    PasswordChangeRequiredFilter.ClaimType,
                    PasswordChangeRequiredFilter.ClaimValue));
            }

            // Issuer și Audience sunt emise explicit pentru că sunt și validate
            // explicit la primire (vezi Program.cs). Un token fără ele ar fi
            // respins de propriul server - de aceea cele două locuri citesc
            // aceleași JwtOptions.
            var token = new JwtSecurityToken(
                issuer:             _jwt.Issuer,
                audience:           _jwt.Audience,
                claims:             claims,
                notBefore:          DateTime.UtcNow,
                expires:            expires,
                signingCredentials: _credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>Token opac base64url, fără padding. Folosit pentru refresh și pentru provocarea 2FA.</summary>
        private static string GenerateOpaqueToken(int byteLength)
        {
            var bytes = RandomNumberGenerator.GetBytes(byteLength);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
