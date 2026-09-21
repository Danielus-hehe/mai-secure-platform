using MAI.Api.Options;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Services
{
    public interface IPasswordResetService
    {
        /// <summary>
        /// Generează un link de resetare pentru cont și îl trimite pe email.
        /// Parola actuală rămâne valabilă până când titularul stabilește una nouă.
        /// </summary>
        Task<PasswordResetRequestResult> RequestAsync(Guid userId, CancellationToken ct = default);

        /// <summary>Datele afișate pe pagina de resetare, dacă tokenul e valid.</summary>
        Task<PasswordResetTokenInfo?> GetTokenInfoAsync(string token, CancellationToken ct = default);

        /// <summary>Consumă tokenul și stabilește parola nouă.</summary>
        Task<PasswordResetResult> CompleteAsync(string token, string newPassword, CancellationToken ct = default);
    }

    public enum PasswordResetRequestStatus
    {
        Sent, UserNotFound, NoEmail, Inactive, SmtpNotConfigured, SendFailed,

        /// <summary>Cont de domeniu: parola se schimbă în Active Directory.</summary>
        DirectoryAccount,
    }

    public sealed record PasswordResetRequestResult(
        PasswordResetRequestStatus Status, string Username, string Email, DateTime? ExpiresAt);

    public sealed record PasswordResetTokenInfo(string Username, string FullName, bool HasKeys, DateTime ExpiresAt);

    public sealed record PasswordResetResult(
        bool Success,
        string? Error = null,
        IReadOnlyList<string>? Errors = null,
        Guid? UserId = null,
        string? Username = null,
        bool KeysCleared = false,
        int SessionsClosed = 0);

    /// <summary>
    /// Resetarea parolei inițiată de administrator, finalizată de titular.
    ///
    /// Diferența față de resetarea cu parolă temporară: administratorul nu află
    /// niciodată parola nouă. Asta contează pentru E2EE - cheile generate după
    /// resetare se încuie cu o parolă pe care o știe doar titularul, deci nu mai
    /// e nevoie de pasul intermediar „schimbă parola temporară”.
    /// </summary>
    public class PasswordResetService : IPasswordResetService
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _email;
        private readonly IPasswordHasher _hasher;
        private readonly PasswordPolicy _policy;
        private readonly Argon2Options _argon2;
        private readonly ISessionService _sessions;
        private readonly PasswordResetOptions _options;
        private readonly IConfiguration _config;
        private readonly ILogger<PasswordResetService> _logger;

        public PasswordResetService(
            AppDbContext db,
            IEmailService email,
            IPasswordHasher hasher,
            PasswordPolicy policy,
            Argon2Options argon2,
            ISessionService sessions,
            PasswordResetOptions options,
            IConfiguration config,
            ILogger<PasswordResetService> logger)
        {
            _db       = db;
            _email    = email;
            _hasher   = hasher;
            _policy   = policy;
            _argon2   = argon2;
            _sessions = sessions;
            _options  = options;
            _config   = config;
            _logger   = logger;
        }

        public async Task<PasswordResetRequestResult> RequestAsync(Guid userId, CancellationToken ct = default)
        {
            var user = await _db.Users.FindAsync([userId], ct);
            if (user is null)
                return new(PasswordResetRequestStatus.UserNotFound, "", "", null);
            if (string.IsNullOrWhiteSpace(user.Email))
                return new(PasswordResetRequestStatus.NoEmail, user.Username, "", null);
            if (!user.IsActive)
                return new(PasswordResetRequestStatus.Inactive, user.Username, user.Email, null);

            // Un cont de domeniu nu are parolă la noi. Un link de resetare i-ar
            // scrie un hash local pe care autentificarea nu îl consultă
            // niciodată: utilizatorul ar stabili o parolă care nu funcționează.
            if (user.IsDirectoryAccount)
                return new(PasswordResetRequestStatus.DirectoryAccount, user.Username, user.Email, null);
            if (!_email.IsConfigured)
                return new(PasswordResetRequestStatus.SmtpNotConfigured, user.Username, user.Email, null);

            // Un token nou îl invalidează pe cel anterior: rămâne valabil doar
            // ultimul link trimis.
            var raw = UrlSafeTokens.Generate();
            user.PasswordResetToken       = UrlSafeTokens.Hash(raw);
            user.PasswordResetTokenExpiry = DateTime.UtcNow.Add(_options.TokenLifetime);
            await _db.SaveChangesAsync(ct);

            var baseUrl = (_config["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
            var link    = $"{baseUrl}/reset-password?token={Uri.EscapeDataString(raw)}";

            var sent = await _email.SendPasswordResetEmailAsync(
                user.Email, user.FullName ?? user.Username, link, _options.TokenLifetime, user.HasKeys, ct);

            if (!sent)
            {
                _logger.LogWarning("Linkul de resetare pentru {User} NU a fost trimis (SMTP indisponibil).", user.Username);
                return new(PasswordResetRequestStatus.SendFailed, user.Username, user.Email, user.PasswordResetTokenExpiry);
            }

            _logger.LogInformation("Link de resetare trimis pentru {User}, expiră la {Expiry:u}.",
                user.Username, user.PasswordResetTokenExpiry);

            return new(PasswordResetRequestStatus.Sent, user.Username, user.Email, user.PasswordResetTokenExpiry);
        }

        public async Task<PasswordResetTokenInfo?> GetTokenInfoAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var hash = UrlSafeTokens.Hash(token);
            var now  = DateTime.UtcNow;

            return await _db.Users
                .Where(u => u.PasswordResetToken == hash && u.PasswordResetTokenExpiry > now && u.IsActive)
                .Select(u => new PasswordResetTokenInfo(
                    u.Username,
                    u.FullName ?? u.Username,
                    u.PublicKeyEncryption != null,
                    u.PasswordResetTokenExpiry!.Value))
                .FirstOrDefaultAsync(ct);
        }

        public async Task<PasswordResetResult> CompleteAsync(string token, string newPassword, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new(false, "Linkul de resetare este incomplet.");

            var hash = UrlSafeTokens.Hash(token);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.PasswordResetToken == hash, ct);

            if (user is null)
                return new(false, "Linkul de resetare nu este valid sau a fost deja folosit. " +
                                  "Cereți administratorului un link nou.");

            if (user.PasswordResetTokenExpiry is null || user.PasswordResetTokenExpiry < DateTime.UtcNow)
                return new(false, "Linkul de resetare a expirat. Cereți administratorului un link nou.");

            if (!user.IsActive)
                return new(false, "Contul este dezactivat. Contactați administratorul.");

            var validation = _policy.Validate(newPassword, user.Username);
            if (!validation.IsValid)
                return new(false, validation.Message, validation.Errors);

            var profile = user.Role >= UserRole.SefDirectie ? _argon2.PrivilegedProfile : _argon2.DefaultProfile;
            user.PasswordHash = await _hasher.HashPasswordAsync(newPassword, profile, ct);

            // Linkul e de unică folosință.
            user.PasswordResetToken       = null;
            user.PasswordResetTokenExpiry = null;

            // Parola a fost aleasă de titular, nu de administrator: nu mai e
            // nevoie de schimbarea forțată. Accesul la linkul din email dovedește
            // și controlul adresei, deci o invitație neterminată se consideră
            // îndeplinită.
            user.MustChangePassword    = false;
            user.EmailConfirmed        = true;
            user.InvitationToken       = null;
            user.InvitationTokenExpiry = null;
            user.FailedLoginAttempts   = 0;
            user.LockoutEndsAt         = null;

            // Cheile private erau încuiate cu parola veche, necunoscută aici.
            var keysCleared = user.ClearEncryptionKeys();

            var closed = await _sessions.RevokeAllAsync(user.Id, "resetare parola prin email", exceptSessionId: null, ct);

            await _db.SaveChangesAsync(ct);

            // Confirmarea pleacă după salvare: dacă titularul nu a cerut nimic,
            // emailul ăsta e alarma lui.
            await _email.SendPasswordChangedEmailAsync(user.Email, user.FullName ?? user.Username, DateTime.UtcNow, ct);

            return new(true, UserId: user.Id, Username: user.Username, KeysCleared: keysCleared, SessionsClosed: closed);
        }
    }
}
