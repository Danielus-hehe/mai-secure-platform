using System.Security.Cryptography;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MAI.Api.Services
{
    /// <summary>
    /// Implementare <see cref="IInvitationService"/> care folosește MailKit (prin
    /// <see cref="IEmailService"/>) și Argon2id (prin <see cref="IPasswordHasher"/>).
    ///
    /// Tokenul brut (Base64Url, 256 bit) ajunge în email și nu se loghează.
    /// În DB se stochează SHA-256(token) - același principiu ca la refresh token.
    ///
    /// Parola setată de utilizator trece prin <see cref="PasswordPolicy"/> și e
    /// hashată cu profilul Argon2 corespunzător rolului.
    /// </summary>
    public class InvitationService : IInvitationService
    {
        /// <summary>Tokenul este valabil 72 de ore de la trimitere.</summary>
        private static readonly TimeSpan TokenValidity = TimeSpan.FromHours(72);

        private readonly AppDbContext              _db;
        private readonly IEmailService             _email;
        private readonly IPasswordHasher           _hasher;
        private readonly PasswordPolicy            _policy;
        private readonly Argon2Options             _argon2;
        private readonly IConfiguration            _config;
        private readonly ILogger<InvitationService> _logger;

        public InvitationService(
            AppDbContext               db,
            IEmailService              email,
            IPasswordHasher            hasher,
            PasswordPolicy             policy,
            Argon2Options              argon2,
            IConfiguration             config,
            ILogger<InvitationService> logger)
        {
            _db     = db;
            _email  = email;
            _hasher = hasher;
            _policy = policy;
            _argon2 = argon2;
            _config = config;
            _logger = logger;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Trimitere invitație
        // ═════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<bool> SendInvitationAsync(Guid userId, CancellationToken ct = default)
        {
            var user = await _db.Users.FindAsync([userId], ct)
                ?? throw new InvalidOperationException($"Userul {userId} nu există.");

            if (string.IsNullOrWhiteSpace(user.Email))
                throw new InvalidOperationException(
                    $"Userul {userId} nu are adresă de email configurată.");

            // Generăm tokenul nou (suprascrie orice token anterior).
            var rawToken = GenerateRawToken();

            user.InvitationToken       = HashToken(rawToken);
            user.InvitationTokenExpiry = DateTime.UtcNow.Add(TokenValidity);
            user.EmailConfirmed        = false;

            await _db.SaveChangesAsync(ct);

            var baseUrl = (_config["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
            var link    = $"{baseUrl}/confirm-account?token={Uri.EscapeDataString(rawToken)}";

            // IEmailService nu aruncă: întoarce false la SMTP neconfigurat sau căzut.
            // Înainte rezultatul era ignorat și logul spunea „Invitație trimisă”
            // chiar când emailul nu plecase.
            var sent = await _email.SendInvitationEmailAsync(
                user.Email,
                user.FullName ?? user.Username,
                link,
                ct);

            if (sent)
            {
                _logger.LogInformation(
                    "Invitație trimisă pentru {UserId} ({Email}), expiră la {Expiry:u}",
                    userId, user.Email, user.InvitationTokenExpiry);
            }
            else
            {
                _logger.LogWarning(
                    "Invitația pentru {UserId} ({Email}) NU a fost trimisă (SMTP neconfigurat sau indisponibil). " +
                    "Tokenul e salvat; administratorul o poate retrimite din pagina Utilizatori.",
                    userId, user.Email);
            }

            return sent;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Confirmare invitație
        // ═════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<InvitationResult> ConfirmAsync(
            string token, string newPassword, CancellationToken ct = default)
        {
            var hashed = HashToken(token);

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.InvitationToken == hashed, ct);

            if (user is null)
                return new InvitationResult(false,
                    "Token invalid. Verificați linkul sau solicitați un nou email de activare.");

            if (user.InvitationTokenExpiry < DateTime.UtcNow)
                return new InvitationResult(false,
                    "Linkul de activare a expirat (72 de ore). " +
                    "Contactați administratorul pentru a-l retrimite.");

            // Parola trece prin aceeași politică ca la crearea contului.
            var validation = _policy.Validate(newPassword, user.Username);
            if (!validation.IsValid)
                return new InvitationResult(false, validation.Message, validation.Errors);

            // Profilul Argon2 după rol: conturile privilegiate primesc cost mai mare.
            var profile = user.Role >= UserRole.SefDirectie
                ? _argon2.PrivilegedProfile
                : _argon2.DefaultProfile;

            user.PasswordHash          = await _hasher.HashPasswordAsync(newPassword, profile, ct);
            user.EmailConfirmed        = true;
            user.MustChangePassword    = false;
            user.InvitationToken       = null;
            user.InvitationTokenExpiry = null;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Cont activat pentru {UserId} ({Username})", user.Id, user.Username);

            return new InvitationResult(true);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Verificare token (pentru frontend)
        // ═════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<InvitationTokenInfo?> GetTokenInfoAsync(
            string token, CancellationToken ct = default)
        {
            var hashed = HashToken(token);

            return await _db.Users
                .Where(u => u.InvitationToken == hashed
                         && u.InvitationTokenExpiry > DateTime.UtcNow)
                .Select(u => new InvitationTokenInfo(u.Username, u.Email))
                .FirstOrDefaultAsync(ct);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Utilitare
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Token aleatoriu 256 bit, Base64Url fără padding.</summary>
        private static string GenerateRawToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// SHA-256 hex al tokenului - stocat în DB.
        /// Tokenul brut nu se loghează și nu ajunge în DB.
        /// </summary>
        private static string HashToken(string raw)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
