using MAI.Api.Security;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage;
using MAI.BusinessLogic.Transfers;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Transferuri criptate end-to-end.
    ///
    /// Modelul de date, după runda „dovadă de primire per destinatar”:
    ///   • FileTransfer — fișierul (cifrotext în depozit), plicul comun (IV,
    ///     semnătura, cheia împachetată pentru expeditor), politica aleasă de
    ///     expeditor (categorie, expirare, AllowForward) și starea agregată.
    ///   • TransferRecipient — câte un rând pentru fiecare destinatar: cheia de
    ///     fișier împachetată pentru el și propria confirmare de primire.
    ///
    /// Serverul nu participă la niciun pas criptografic: primește și servește
    /// blocuri opace. Regulile de stare sunt în TransferRules (testate separat).
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class TransfersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly TransferPolicyOptions _policy;
        private readonly IEmailService _email;
        private readonly ILogger<TransfersController> _logger;

        public TransfersController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            TransferPolicyOptions policy,
            IEmailService email,
            ILogger<TransfersController> logger)
        {
            _context        = context;
            _storage        = storage;
            _storageOptions = storageOptions;
            _policy         = policy;
            _email          = email;
            _logger         = logger;
        }

        private Guid CurrentUserId =>
            Guid.Parse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        private string CurrentUsername =>
            HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        private bool IsAdministrator =>
            HttpContext.User.IsInRole(nameof(UserRole.Administrator))
            || HttpContext.User.FindFirst(ClaimTypes.Role)?.Value == ((int)UserRole.Administrator).ToString();

        private const int MaxWrappedKeyChars   = 600;
        private const int MaxRevokeReasonChars = 256;
        private const int MaxIvChars           = 32;

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Transfers/policy
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Limitele pe care le aplică serverul, ca formularul de trimitere să le
        /// afișeze din aceeași sursă (Transfers:* din .env / appsettings).
        /// </summary>
        [HttpGet("policy")]
        public IActionResult GetPolicy() =>
            Ok(new TransferPolicyDto(_policy.DefaultExpiryDays, _policy.MaxExpiryDays, _policy.MaxRecipients));

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Transfers
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] string? direction,
            [FromQuery] TransferCategory? category,
            [FromQuery] string sortBy = "createdAt",
            [FromQuery] string sortDir = "desc",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken ct = default)
        {
            var userId     = CurrentUserId;
            var now        = DateTime.UtcNow;
            var pagination = new PaginationQuery { Page = page, PageSize = pageSize };

            // Transferurile șterse logic nu apar în liste. Rândurile rămân în bază,
            // cu dovezile de primire, pentru audit.
            var query = _context.FileTransfers
                .AsNoTracking()
                .Where(t => t.DeletedAt == null)
                .Where(t => t.SenderId == userId || t.Recipients.Any(r => r.UserId == userId));

            if (string.Equals(direction, "sent", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.SenderId == userId);
            else if (string.Equals(direction, "received", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.Recipients.Any(r => r.UserId == userId));

            if (category.HasValue && TransferRules.IsValidCategory(category.Value))
                query = query.Where(t => t.Category == category.Value);

            // Filtrul pe stare folosește aceeași regulă ca afișarea: un transfer
            // în așteptare trecut de termen e „Expirat”, nu „În așteptare”.
            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<TransferStatus>(status, ignoreCase: true, out var statusEnum) &&
                Enum.IsDefined(statusEnum))
            {
                query = statusEnum switch
                {
                    TransferStatus.Pending => query.Where(t =>
                        t.Status == TransferStatus.Pending && (t.ExpiresAt == null || t.ExpiresAt > now)),
                    TransferStatus.Expired => query.Where(t =>
                        t.Status == TransferStatus.Expired ||
                        (t.Status == TransferStatus.Pending && t.ExpiresAt != null && t.ExpiresAt <= now)),
                    _ => query.Where(t => t.Status == statusEnum),
                };
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                query = query.Where(t =>
                    EF.Functions.ILike(t.FileName, term) ||
                    (t.Sender != null && (
                        EF.Functions.ILike(t.Sender.Username, term) ||
                        (t.Sender.FullName != null && EF.Functions.ILike(t.Sender.FullName, term)))) ||
                    t.Recipients.Any(r => r.User != null && (
                        EF.Functions.ILike(r.User.Username, term) ||
                        (r.User.FullName != null && EF.Functions.ILike(r.User.FullName, term)))));
            }

            var total = await query.CountAsync(ct);

            // AsSplitQuery: cu Include pe colecție, o singură interogare ar
            // multiplica fiecare transfer cu numărul destinatarilor (produs
            // cartezian). Sortarea are Id ca departajare, ca paginile să fie
            // stabile între cele două interogări.
            var raw = await ApplySort(query, sortBy, sortDir)
                .Skip(pagination.Skip)
                .Take(pagination.PageSize)
                .Include(t => t.Sender)
                .Include(t => t.Recipients).ThenInclude(r => r.User)
                .Include(t => t.Recipients).ThenInclude(r => r.ForwardedBy)
                .AsSplitQuery()
                .ToListAsync(ct);

            var isAdmin = IsAdministrator;
            var items   = raw.Select(t => ToDto(t, userId, isAdmin, now)).ToList();

            return Ok(PagedResult<TransferDto>.Create(items, total, pagination));
        }

        private static IQueryable<FileTransfer> ApplySort(
            IQueryable<FileTransfer> query, string sortBy, string sortDir)
        {
            var asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
            IOrderedQueryable<FileTransfer> ordered = sortBy?.ToLowerInvariant() switch
            {
                "filename" => asc ? query.OrderBy(t => t.FileName)  : query.OrderByDescending(t => t.FileName),
                "filesize" => asc ? query.OrderBy(t => t.FileSize)  : query.OrderByDescending(t => t.FileSize),
                "status"   => asc ? query.OrderBy(t => t.Status)    : query.OrderByDescending(t => t.Status),
                "category" => asc ? query.OrderBy(t => t.Category)  : query.OrderByDescending(t => t.Category),
                _          => asc ? query.OrderBy(t => t.CreatedAt) : query.OrderByDescending(t => t.CreatedAt),
            };
            return ordered.ThenBy(t => t.Id);
        }

        /// <summary>
        /// Proiecția unui transfer pentru utilizatorul curent.
        ///
        /// Cine vede confirmările: expeditorul le vede pe toate; cel care a făcut
        /// un forward le vede pe ale destinatarilor adăugați de el; fiecare
        /// destinatar o vede pe a lui. Un destinatar nu află cine dintre colegi a
        /// deschis deja documentul — asta e informația expeditorului.
        /// </summary>
        private static TransferDto ToDto(FileTransfer t, Guid userId, bool isAdmin, DateTime now)
        {
            var isSender = t.SenderId == userId;
            var mine     = t.Recipients.FirstOrDefault(r => r.UserId == userId);
            var effective = TransferRules.EffectiveStatus(t, now);

            var recipients = t.Recipients
                .OrderBy(r => r.SentAt).ThenBy(r => r.User?.FullName ?? r.User?.Username)
                .Select(r =>
                {
                    var visible = isSender || r.UserId == userId || r.ForwardedById == userId;
                    return new TransferRecipientDto
                    {
                        UserId          = r.UserId,
                        Name            = DisplayName(r.User),
                        Department      = r.User?.Department ?? string.Empty,
                        SentAt          = r.SentAt,
                        ForwardedById   = r.ForwardedById,
                        ForwardedByName = r.ForwardedById.HasValue ? DisplayName(r.ForwardedBy) : null,
                        ReceiptVisible  = visible,
                        DownloadedAt    = visible ? r.DownloadedAt   : null,
                        SignatureValid  = visible ? r.SignatureValid : null,
                    };
                })
                .ToList();

            return new TransferDto
            {
                Id               = t.Id,
                FileName         = t.FileName,
                FileSize         = t.FileSize,
                CiphertextSize   = t.CiphertextSize,
                Sha256           = t.ChecksumSHA256,
                SenderId         = t.SenderId,
                SenderName       = DisplayName(t.Sender),
                SenderDepartment = t.Sender?.Department ?? string.Empty,
                Status           = effective.ToString(),
                CreatedAt        = t.CreatedAt,
                ExpiresAt        = t.ExpiresAt,
                RevokedAt        = t.RevokedAt,
                RevokedReason    = t.RevokedReason,
                Category         = t.Category,
                AllowForward     = t.AllowForward,
                IsMine           = isSender,
                IsEncrypted      = t.IsEncrypted,
                CryptoSuite      = t.CryptoSuite,

                Recipients       = recipients,
                RecipientCount   = recipients.Count,
                DownloadedCount  = isSender ? t.Recipients.Count(r => r.DownloadedAt.HasValue) : null,
                IsRecipient      = mine is not null,
                MyDownloadedAt   = mine?.DownloadedAt,
                MySignatureValid = mine?.SignatureValid,

                CanDownload      = TransferRules.IsContentAvailable(t, now),
                CanForward       = TransferRules.CanForward(t, userId, mine is not null, now),
                CanRevoke        = TransferRules.CanRevoke(t, userId, now),
                CanDelete        = (isSender || isAdmin) && effective != TransferStatus.Pending,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Transfers
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Trimite un fișier criptat către unul sau mai mulți destinatari.
        ///
        /// Cifrotextul și IV-ul sunt comune; DEK-ul vine împachetat separat
        /// pentru fiecare destinatar (Recipients[i].UserId + EncryptedKeyForUser)
        /// și o dată pentru expeditor. Un singur obiect în depozit, oricâți
        /// destinatari — nu N copii ale aceluiași fișier.
        /// </summary>
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]
        public async Task<IActionResult> Upload(
            [FromForm] UploadTransferRequest request,
            CancellationToken ct)
        {
            var senderId = CurrentUserId;
            var now      = DateTime.UtcNow;

            if (request.File is null || request.File.Length == 0)
                return BadRequest(new { message = "Cifrotextul este obligatoriu." });

            if (request.File.Length > _storageOptions.MaxFileSizeBytes)
                return BadRequest(new { message = $"Fișierul depășește limita de {_storageOptions.MaxFileSizeMb} MB." });

            // Enum.IsDefined: model binding-ul acceptă orice întreg („Category=42”)
            // și l-ar fi salvat ca atare; interfața ar fi afișat o categorie
            // inexistentă, iar filtrele n-ar mai fi găsit transferul.
            if (!TransferRules.IsValidCategory(request.Category))
                return BadRequest(new { message = "Categoria transferului este invalidă." });

            var recipientInputs = request.ResolveRecipients();
            if (!TryValidateRecipientInputs(recipientInputs, senderId, _policy.MaxRecipients, out var inputError))
                return BadRequest(new { message = inputError });

            if (!TryValidateEnvelope(request, out var envelopeError))
                return BadRequest(new { message = envelopeError });

            var expiry = TransferRules.ResolveExpiry(request.ExpiresAt, _policy, now);
            if (!expiry.Success)
                return BadRequest(new { message = expiry.Error });

            var recipientIds = recipientInputs.Select(r => r.UserId).ToList();
            var recipients = await _context.Users
                .AsNoTracking()
                .Where(u => recipientIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.FullName, u.Email, u.IsActive, u.PublicKeyEncryption })
                .ToListAsync(ct);

            foreach (var id in recipientIds)
            {
                var user = recipients.FirstOrDefault(u => u.Id == id);
                if (user is null)
                    return BadRequest(new { message = "Unul dintre destinatari nu a fost găsit." });
                if (!user.IsActive)
                    return BadRequest(new { message = $"Utilizatorul @{user.Username} are contul dezactivat." });
                if (string.IsNullOrEmpty(user.PublicKeyEncryption))
                    return BadRequest(new
                    {
                        message = $"Utilizatorul @{user.Username} nu și-a generat încă cheile. " +
                                  "Nu i se pot trimite fișiere criptate.",
                    });
            }

            var senderInfo = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == senderId)
                .Select(u => new { u.PublicKeyEncryption, u.Department })
                .FirstOrDefaultAsync(ct);

            if (senderInfo?.PublicKeyEncryption is null)
                return BadRequest(new { message = "Nu ai chei înregistrate. Generează-le înainte de a trimite fișiere." });

            var safeName = Path.GetFileName(request.FileName ?? request.File.FileName);
            if (string.IsNullOrWhiteSpace(safeName))
                return BadRequest(new { message = "Numele fișierului este invalid." });
            if (safeName.Length > 260) safeName = safeName[..260];

            await using var upload = request.File.OpenReadStream();
            var computedHash = await ComputeSha256HexAsync(upload, ct);

            if (!string.Equals(computedHash, request.CiphertextSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Amprentă necorespunzătoare la upload de la {User}: declarat {Declared}, calculat {Computed}",
                    CurrentUsername, request.CiphertextSha256, computedHash);
                return BadRequest(new { message = "Amprenta SHA-256 a cifrotextului nu corespunde." });
            }

            if (!upload.CanSeek)
                return StatusCode(500, new { message = "Stream de upload nerepozitionabil." });
            upload.Position = 0;

            var transferId = Guid.NewGuid();
            var storageKey = BuildStorageKey(transferId, senderInfo.Department);

            await _storage.PutAsync(storageKey, upload, request.File.Length, "application/octet-stream", ct);

            var transfer = new FileTransfer
            {
                Id                    = transferId,
                SenderId              = senderId,
                FileName              = safeName,
                StorageKey            = storageKey,
                FileSize              = request.PlaintextSize > 0 ? request.PlaintextSize : request.File.Length,
                CiphertextSize        = request.File.Length,
                ChecksumSHA256        = computedHash,
                Status                = TransferStatus.Pending,
                CreatedAt             = now,
                ExpiresAt             = expiry.Value,
                Category              = request.Category,
                AllowForward          = request.AllowForward,
                EncryptionIv          = request.Iv,
                EncryptedKeyForSender = request.EncryptedKeyForSender,
                SenderSignature       = request.Signature,
                CryptoSuite           = request.Suite,
                IsEncrypted           = true,
            };

            foreach (var input in recipientInputs)
            {
                transfer.Recipients.Add(new TransferRecipient
                {
                    TransferId          = transferId,
                    UserId              = input.UserId,
                    EncryptedKeyForUser = input.EncryptedKeyForUser,
                    ForwardedById       = null,   // destinatar direct
                    SentAt              = now,
                });
            }

            var names = recipientIds
                .Select(id => recipients.First(u => u.Id == id))
                .Select(u => u.FullName ?? u.Username)
                .ToList();

            _context.FileTransfers.Add(transfer);
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = senderId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileUpload,
                Details   = $"Fisier criptat trimis '{safeName}' ({request.File.Length} octeti) " +
                            $"catre {names.Count} destinatar(i): {string.Join(", ", names)}; " +
                            $"suita {request.Suite}, categorie {request.Category}, " +
                            $"forward {(request.AllowForward ? "permis" : "interzis")}, " +
                            $"expira {expiry.Value:yyyy-MM-dd HH:mm} UTC, cheie depozit: {storageKey}",
                IpAddress = CallerIp,
                Timestamp = now,
            });

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Salvarea transferului a eșuat. Se retrage obiectul {Key} din depozit.", storageKey);
                try { await _storage.DeleteAsync(storageKey, CancellationToken.None); }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx, "Retragerea obiectului {Key} a eșuat.", storageKey);
                }
                throw;
            }

            NotifyRecipients(
                recipients.Select(u => (u.Email, u.FullName ?? u.Username, u.Username)),
                transfer);

            return Ok(new
            {
                id         = transfer.Id,
                sha256     = computedHash,
                expiresAt  = transfer.ExpiresAt,
                category   = transfer.Category.ToString(),
                recipients = names.Count,
                message    = names.Count == 1
                    ? $"Fișierul '{safeName}' a fost trimis criptat."
                    : $"Fișierul '{safeName}' a fost trimis criptat către {names.Count} destinatari.",
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Transfers/{id}/envelope
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("{id:guid}/envelope")]
        public async Task<IActionResult> GetEnvelope(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;

            var transfer = await _context.FileTransfers
                .AsNoTracking()
                .Include(t => t.Sender)
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            var isSender = transfer.SenderId == userId;
            var mine = isSender
                ? null
                : await _context.TransferRecipients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.TransferId == id && r.UserId == userId, ct);

            if (!isSender && mine is null)
                return Forbid();

            var unavailable = UnavailableResult(transfer);
            if (unavailable is not null) return unavailable;

            if (!transfer.IsEncrypted)
                return Ok(new
                {
                    id          = transfer.Id,
                    fileName    = transfer.FileName,
                    isEncrypted = false,
                    downloadUrl = (string?)null,
                    message     = "Transfer necriptat, dinaintea migrării la criptare end-to-end.",
                });

            // Fiecare parte primește cheia împachetată PENTRU EA, nu pentru ceilalți.
            var wrappedKeyForMe = isSender ? transfer.EncryptedKeyForSender : mine!.EncryptedKeyForUser;

            if (string.IsNullOrEmpty(wrappedKeyForMe))
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    message = "Pentru acest transfer nu există o cheie împachetată pentru contul dumneavoastră.",
                });

            string? downloadUrl = null;
            if (_storageOptions.UsePresignedDownload && _storage.SupportsPresignedUrls)
                downloadUrl = await _storage.TryCreatePresignedDownloadUrlAsync(
                    transfer.StorageKey, _storageOptions.PresignedUrlLifetime, ct);

            return Ok(new
            {
                id                     = transfer.Id,
                fileName               = transfer.FileName,
                fileSize               = transfer.FileSize,
                ciphertextSize         = transfer.CiphertextSize,
                isEncrypted            = true,
                iv                     = transfer.EncryptionIv,
                wrappedKeyForMe,
                signature              = transfer.SenderSignature,
                ciphertextSha256       = transfer.ChecksumSHA256,
                suite                  = transfer.CryptoSuite,
                senderId               = transfer.SenderId,
                senderName             = DisplayName(transfer.Sender),
                senderPublicKeySigning = transfer.Sender?.PublicKeySigning,
                downloadUrl,
                expiresAt              = transfer.ExpiresAt,
                isRecipient            = mine is not null,
                alreadyConfirmed       = mine?.DownloadedAt is not null,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Transfers/{id}/content
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("{id:guid}/content")]
        public async Task<IActionResult> GetContent(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;

            var transfer = await _context.FileTransfers
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            var hasAccess = transfer.SenderId == userId
                || await _context.TransferRecipients.AnyAsync(r => r.TransferId == id && r.UserId == userId, ct);

            if (!hasAccess)
                return Forbid();

            var unavailable = UnavailableResult(transfer);
            if (unavailable is not null) return unavailable;

            if (string.IsNullOrEmpty(transfer.StorageKey))
                return NotFound(new { message = "Transferul nu are conținut în depozitul curent." });

            Stream stream;
            try
            {
                stream = await _storage.OpenReadAsync(transfer.StorageKey, ct);
            }
            catch (FileNotFoundException)
            {
                _logger.LogError("Rând de transfer fără obiect în depozit: {Id} → {Key}", transfer.Id, transfer.StorageKey);
                return NotFound(new { message = "Conținutul nu mai există în depozit." });
            }

            return File(stream, "application/octet-stream", $"{transfer.Id}.enc");
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Transfers/{id}/forward
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Adaugă destinatari la un transfer existent.
        ///
        /// Serverul nu atinge conținutul: clientul despachetează DEK-ul cu cheia
        /// lui privată, îl re-împachetează cu cheia publică a fiecărui destinatar
        /// nou și trimite aici doar blocurile RSA-OAEP rezultate.
        ///
        /// Cine poate face forward (TransferRules.CanForward):
        ///   • expeditorul — întotdeauna, cât timp conținutul există;
        ///   • un destinatar — doar dacă expeditorul a bifat AllowForward.
        /// </summary>
        [HttpPost("{id:guid}/forward")]
        public async Task<IActionResult> Forward(
            Guid id,
            [FromBody] ForwardTransferRequest? request,
            CancellationToken ct)
        {
            if (request?.Recipients is not { Count: > 0 })
                return BadRequest(new { message = "Lista destinatarilor nu poate fi goală." });

            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;

            var transfer = await _context.FileTransfers
                .Include(t => t.Recipients)
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            var isSender    = transfer.SenderId == userId;
            var isRecipient = transfer.Recipients.Any(r => r.UserId == userId);

            if (!isSender && !isRecipient)
                return Forbid();

            var unavailable = UnavailableResult(transfer);
            if (unavailable is not null) return unavailable;

            if (!TransferRules.CanForward(transfer, userId, isRecipient, now))
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Expeditorul nu a permis redistribuirea acestui transfer. " +
                              "Cereți-i să îl trimită direct colegului.",
                });

            var remaining = _policy.MaxRecipients - transfer.Recipients.Count;
            if (!TryValidateRecipientInputs(request.Recipients, userId, Math.Max(remaining, 0), out var inputError))
                return BadRequest(new
                {
                    message = remaining <= 0
                        ? $"Transferul are deja numărul maxim de destinatari ({_policy.MaxRecipients})."
                        : inputError,
                });

            var incomingIds = request.Recipients.Select(r => r.UserId).ToList();

            if (incomingIds.Contains(transfer.SenderId))
                return BadRequest(new { message = "Expeditorul are deja acces la acest transfer." });

            var duplicates = incomingIds.Where(uid => transfer.Recipients.Any(r => r.UserId == uid)).ToList();
            if (duplicates.Count > 0)
                return Conflict(new
                {
                    message      = "Unul sau mai mulți utilizatori sunt deja destinatari ai acestui transfer.",
                    duplicateIds = duplicates,
                });

            var validUsers = await _context.Users
                .AsNoTracking()
                .Where(u => incomingIds.Contains(u.Id) && u.IsActive && u.PublicKeyEncryption != null)
                .Select(u => new { u.Id, u.Username, u.FullName, u.Email })
                .ToListAsync(ct);

            if (validUsers.Count != incomingIds.Count)
                return BadRequest(new
                {
                    message    = "Unul sau mai mulți destinatari nu există, sunt inactivi sau nu și-au generat cheile.",
                    invalidIds = incomingIds.Except(validUsers.Select(u => u.Id)).ToList(),
                });

            foreach (var r in request.Recipients)
            {
                transfer.Recipients.Add(new TransferRecipient
                {
                    TransferId          = id,
                    UserId              = r.UserId,
                    EncryptedKeyForUser = r.EncryptedKeyForUser,
                    ForwardedById       = userId,
                    SentAt              = now,
                });
            }

            // Un destinatar nou nu a descărcat încă: dacă toți ceilalți terminaseră,
            // transferul redevine activ.
            transfer.Status = TransferRules.AggregateStatus(transfer.Status, transfer.Recipients);

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.TransferForwarded,
                Details   = $"Forward transfer '{transfer.FileName}' (id {transfer.Id}) de catre " +
                            $"{(isSender ? "expeditor" : "destinatar")} catre " +
                            string.Join(", ", validUsers.Select(u => u.FullName ?? u.Username)),
                IpAddress = CallerIp,
                Timestamp = now,
            });

            await _context.SaveChangesAsync(ct);

            NotifyRecipients(validUsers.Select(u => (u.Email, u.FullName ?? u.Username, u.Username)), transfer);

            return Ok(new
            {
                message    = $"Transferul a fost redirecționat către {validUsers.Count} destinatar(i).",
                recipients = validUsers.Select(u => new { id = u.Id, name = u.FullName ?? u.Username }),
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Transfers/{id}/confirm
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Dovada de primire a destinatarului curent. Se scrie pe rândul LUI din
        /// TransferRecipients; transferul devine Downloaded doar când au
        /// confirmat toți destinatarii.
        /// </summary>
        [HttpPatch("{id:guid}/confirm")]
        public async Task<IActionResult> Confirm(
            Guid id, [FromBody] ConfirmTransferDto? dto, CancellationToken ct)
        {
            if (dto?.SignatureValid is not bool signatureValid)
                return BadRequest(new { message = "Rezultatul verificării semnăturii (signatureValid) este obligatoriu." });

            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;

            var transfer = await _context.FileTransfers
                .Include(t => t.Recipients)
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            var mine = transfer.Recipients.FirstOrDefault(r => r.UserId == userId);
            if (mine is null)
                return Forbid();

            if (mine.DownloadedAt.HasValue)
                return Ok(new
                {
                    message          = "Primirea era deja confirmată.",
                    alreadyConfirmed = true,
                    downloadedAt     = mine.DownloadedAt,
                    signatureValid   = mine.SignatureValid,
                });

            if (transfer.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a fost retras de expeditor. Primirea nu se mai înregistrează.",
                });

            if (transfer.Status == TransferStatus.Expired || TransferRules.IsPastExpiry(transfer, now))
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a expirat. Primirea nu se mai înregistrează.",
                });

            mine.DownloadedAt   = now;
            mine.SignatureValid = signatureValid;
            transfer.Status     = TransferRules.AggregateStatus(transfer.Status, transfer.Recipients);

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDownload,
                Details   = signatureValid
                    ? $"Fisier descarcat si decriptat '{transfer.FileName}' (id {transfer.Id}), semnatura expeditorului VALIDA"
                    : $"Fisier descarcat '{transfer.FileName}' (id {transfer.Id}), semnatura expeditorului INVALIDA",
                Result    = signatureValid ? AuditResult.Success : AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = now,
            });

            await _context.SaveChangesAsync(ct);

            // Doi destinatari care confirmă simultan citesc fiecare starea
            // dinaintea celuilalt și niciunul nu vede „toți au confirmat”. O
            // singură instrucțiune SQL, rulată după commit, închide fereastra.
            if (transfer.Status == TransferStatus.Pending)
            {
                await _context.FileTransfers
                    .Where(t => t.Id == id
                             && t.Status == TransferStatus.Pending
                             && t.Recipients.All(r => r.DownloadedAt != null))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TransferStatus.Downloaded), ct);
            }

            return Ok(new
            {
                message          = "Primirea a fost confirmată.",
                alreadyConfirmed = false,
                downloadedAt     = mine.DownloadedAt,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Transfers/{id}/revoke
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Retrage transferul: obiectul se șterge din depozit, destinatarii care
        /// nu l-au descărcat nu îl mai pot deschide. Cei care l-au descărcat deja
        /// îl păstrează — și dovada lor de primire rămâne.
        /// </summary>
        [HttpPost("{id:guid}/revoke")]
        public async Task<IActionResult> Revoke(
            Guid id, [FromBody] RevokeTransferDto? dto, CancellationToken ct)
        {
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;

            var transfer = await _context.FileTransfers
                .Include(t => t.Recipients)
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.SenderId != userId)
                return Forbid();

            if (transfer.Status == TransferStatus.Downloaded)
                return Conflict(new
                {
                    message = "Toți destinatarii au descărcat deja fișierul; retragerea nu mai are efect. " +
                              "Fișierul se află pe dispozitivele lor.",
                });

            if (!TransferRules.CanRevoke(transfer, userId, now))
                return Conflict(new { message = "Transferul nu mai este activ (expirat sau retras)." });

            if (!await TryDeleteObjectAsync(transfer, AuditAction.TransferRevoked, "Retragere", ct))
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Fișierul nu a putut fi șters din depozit. Transferul NU a fost retras. Încercați din nou.",
                });

            var alreadyDownloaded = transfer.Recipients.Count(r => r.DownloadedAt.HasValue);

            transfer.Status        = TransferStatus.Revoked;
            transfer.RevokedAt     = now;
            transfer.RevokedReason = Truncate(dto?.Reason, MaxRevokeReasonChars);
            ShredKeys(transfer);

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.TransferRevoked,
                Details   = $"Transfer retras '{transfer.FileName}' (id {transfer.Id}); " +
                            $"{alreadyDownloaded} din {transfer.Recipients.Count} destinatari il descarcasera" +
                            (string.IsNullOrWhiteSpace(transfer.RevokedReason) ? "" : $"; motiv: {transfer.RevokedReason}"),
                Result    = AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = now,
            });

            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message = alreadyDownloaded == 0
                    ? "Transferul a fost retras. Fișierul nu mai poate fi descărcat."
                    : $"Transferul a fost retras pentru destinatarii rămași. {alreadyDownloaded} " +
                      "destinatar(i) îl descărcaseră deja și îl păstrează.",
                alreadyDownloaded,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/Transfers/{id} — ștergere logică
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Scoate transferul din liste și șterge cifrotextul, fără să distrugă
        /// rândul și dovezile de primire.
        ///
        /// Un transfer activ (cu destinatari care nu l-au descărcat) nu se poate
        /// șterge direct: ar dispărea din lista destinatarilor fără explicație.
        /// Se retrage întâi — destinatarii văd „Retras” — apoi se poate șterge.
        /// </summary>
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;
            var now    = DateTime.UtcNow;

            var transfer = await _context.FileTransfers
                .Include(t => t.Recipients)
                .FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.SenderId != userId && !IsAdministrator) return Forbid();

            if (TransferRules.EffectiveStatus(transfer, now) == TransferStatus.Pending)
                return Conflict(new
                {
                    message = "Transferul are destinatari care nu l-au descărcat încă. " +
                              "Retrageți-l mai întâi, ca aceștia să vadă că a fost anulat.",
                });

            if (!await TryDeleteObjectAsync(transfer, AuditAction.FileDeleted, "Stergere", ct))
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Fișierul nu a putut fi șters din depozit. Transferul NU a fost șters. Încercați din nou.",
                });

            transfer.DeletedAt   = now;
            transfer.DeletedById = userId;
            ShredKeys(transfer);

            var downloaded = transfer.Recipients.Count(r => r.DownloadedAt.HasValue);

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDeleted,
                Details   = $"Transfer sters (logic) '{transfer.FileName}' (id {transfer.Id}), " +
                            $"stare {transfer.Status}, {downloaded} din {transfer.Recipients.Count} confirmari pastrate" +
                            (transfer.SenderId != userId ? "; sters de administrator" : ""),
                IpAddress = CallerIp,
                Timestamp = now,
            });

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Transferul a fost șters. Dovezile de primire rămân în jurnal." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>410 dacă transferul nu mai are conținut accesibil; altfel null.</summary>
        private ObjectResult? UnavailableResult(FileTransfer t)
        {
            if (t.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message   = "Transferul a fost retras de expeditor și nu mai poate fi descărcat.",
                    revokedAt = t.RevokedAt,
                    reason    = t.RevokedReason,
                });

            if (t.Status == TransferStatus.Expired || TransferRules.IsPastExpiry(t, DateTime.UtcNow))
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a expirat și nu mai poate fi descărcat.",
                });

            return null;
        }

        /// <summary>
        /// Șterge obiectul din depozit. La eșec consemnează în audit și întoarce
        /// false — apelantul NU schimbă starea transferului, ca interfața să nu
        /// afirme „șters” despre un fișier încă prezent.
        /// </summary>
        private async Task<bool> TryDeleteObjectAsync(
            FileTransfer transfer, AuditAction action, string operation, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(transfer.StorageKey)) return true;

            try
            {
                await _storage.DeleteAsync(transfer.StorageKey, ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} esuata: obiectul {Key} nu a putut fi sters.", operation, transfer.StorageKey);
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId    = CurrentUserId,
                    Username  = CurrentUsername,
                    Action    = action,
                    Details   = $"{operation} esuata pentru '{transfer.FileName}': obiectul nu a putut fi sters din depozit",
                    Result    = AuditResult.Failure,
                    IpAddress = CallerIp,
                    Timestamp = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync(ct);
                return false;
            }
        }

        /// <summary>
        /// Golește cheia de depozit și toate cheile de fișier împachetate. Fără
        /// cifrotext nu mai deschid nimic, dar nu au motiv să rămână în bază.
        /// DownloadedAt și SignatureValid rămân: ele sunt dovada.
        /// </summary>
        private static void ShredKeys(FileTransfer transfer)
        {
            transfer.StorageKey            = string.Empty;
            transfer.EncryptedKeyForSender = null;
            foreach (var r in transfer.Recipients)
                r.EncryptedKeyForUser = string.Empty;
        }

        /// <summary>
        /// Validare comună pentru destinatarii de la trimitere și de la forward:
        /// număr, duplicate, auto-trimitere, format al cheii împachetate.
        /// </summary>
        private static bool TryValidateRecipientInputs(
            IReadOnlyCollection<RecipientKeyInput> inputs, Guid currentUserId, int maxCount, out string error)
        {
            error = string.Empty;

            if (inputs.Count == 0)
            {
                error = "Alegeți cel puțin un destinatar."; return false;
            }
            if (inputs.Count > maxCount)
            {
                error = $"Maxim {maxCount} destinatari pentru acest transfer."; return false;
            }
            if (inputs.Any(r => r.UserId == Guid.Empty))
            {
                error = "Identificator de destinatar invalid."; return false;
            }
            if (inputs.Select(r => r.UserId).Distinct().Count() != inputs.Count)
            {
                error = "Lista conține destinatari duplicați."; return false;
            }
            if (inputs.Any(r => r.UserId == currentUserId))
            {
                error = "Nu vă puteți trimite un fișier dumneavoastră înșivă."; return false;
            }
            foreach (var r in inputs)
            {
                if (!IsValidWrappedKey(r.EncryptedKeyForUser))
                {
                    error = $"Cheia împachetată pentru destinatarul {r.UserId} lipsește sau este invalidă.";
                    return false;
                }
            }
            return true;
        }

        private static bool IsValidWrappedKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxWrappedKeyChars) return false;
            Span<byte> probe = new byte[value.Length];
            return Convert.TryFromBase64String(value, probe, out _);
        }

        private void NotifyRecipients(
            IEnumerable<(string Email, string Name, string Username)> users, FileTransfer transfer)
        {
            var senderName = CurrentUsername;
            foreach (var (email, name, username) in users)
            {
                _ = _email.SendTransferNotificationAsync(
                        toEmail:    email,
                        toName:     name,
                        senderName: senderName,
                        fileName:   transfer.FileName,
                        expiresAt:  transfer.ExpiresAt,
                        ct:         CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            _logger.LogWarning(t.Exception,
                                "Notificarea email pentru transferul {Id} către {User} nu a putut fi trimisă.",
                                transfer.Id, username);
                    }, TaskScheduler.Default);
            }
        }

        private static string DisplayName(User? user) =>
            user is null
                ? "—"
                : string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            return v.Length <= max ? v : v[..max];
        }

        private static string BuildStorageKey(Guid transferId, string? senderDepartment)
        {
            var now  = DateTime.UtcNow;
            var dept = SanitizeDepartment(senderDepartment);
            return $"{dept}/{now:yyyy}/{now:MM}/{transferId:N}.enc";
        }

        private static string SanitizeDepartment(string? department)
        {
            if (string.IsNullOrWhiteSpace(department)) return "general";

            var nfkd = department.Normalize(NormalizationForm.FormD);
            var sb   = new StringBuilder(nfkd.Length);
            var prevWasSep = true;

            foreach (var ch in nfkd)
            {
                if (char.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsAsciiLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    prevWasSep = false;
                }
                else if (!prevWasSep)
                {
                    sb.Append('-');
                    prevWasSep = true;
                }
            }

            if (sb.Length > 0 && sb[^1] == '-') sb.Length--;
            var result = sb.ToString();
            if (string.IsNullOrEmpty(result)) return "general";
            if (result.Length > 50) result = result[..50].TrimEnd('-');
            return string.IsNullOrEmpty(result) ? "general" : result;
        }

        private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken ct)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer    = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                sha.AppendData(buffer, 0, read);
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        private static bool TryValidateEnvelope(UploadTransferRequest r, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(r.Iv) || string.IsNullOrWhiteSpace(r.EncryptedKeyForSender) ||
                string.IsNullOrWhiteSpace(r.Signature) || string.IsNullOrWhiteSpace(r.CiphertextSha256))
            {
                error = "Plic criptografic incomplet."; return false;
            }
            if (r.Iv.Length > MaxIvChars || r.EncryptedKeyForSender.Length > MaxWrappedKeyChars ||
                r.Signature.Length > MaxWrappedKeyChars)
            {
                error = "Câmpurile plicului depășesc dimensiunile așteptate."; return false;
            }
            if (r.CiphertextSha256.Length != 64 || !r.CiphertextSha256.All(Uri.IsHexDigit))
            {
                error = "Amprenta SHA-256 trebuie să fie 64 de caractere hexazecimale."; return false;
            }
            foreach (var (name, value) in new[]
            {
                ("iv", r.Iv), ("encryptedKeyForSender", r.EncryptedKeyForSender), ("signature", r.Signature),
            })
            {
                Span<byte> probe = new byte[value.Length];
                if (!Convert.TryFromBase64String(value, probe, out _))
                {
                    error = $"Câmpul '{name}' nu este base64 valid."; return false;
                }
            }
            if (string.IsNullOrWhiteSpace(r.Suite) || r.Suite.Length > 128)
            {
                error = "Suita criptografică lipsește sau este invalidă."; return false;
            }
            return true;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Modele request
    // ═════════════════════════════════════════════════════════════════════════

    public class UploadTransferRequest
    {
        public IFormFile? File { get; set; }
        public string? FileName { get; set; }
        public long PlaintextSize { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public TransferCategory Category { get; set; } = TransferCategory.General;

        /// <summary>Pot destinatarii redirecționa fișierul? Implicit nu.</summary>
        public bool AllowForward { get; set; }

        /// <summary>
        /// Destinatarii, fiecare cu DEK-ul împachetat pentru el. În multipart:
        /// Recipients[0].UserId, Recipients[0].EncryptedKeyForUser, …
        /// </summary>
        public List<RecipientKeyInput> Recipients { get; set; } = [];

        /// <summary>Formatul vechi, cu un singur destinatar. Păstrat pentru clienți vechi.</summary>
        public string? RecipientId { get; set; }

        /// <summary>Formatul vechi, cu un singur destinatar. Păstrat pentru clienți vechi.</summary>
        public string? EncryptedKeyForRecipient { get; set; }

        public string? Iv { get; set; }
        public string? EncryptedKeyForSender { get; set; }
        public string? Signature { get; set; }
        public string? CiphertextSha256 { get; set; }
        public string? Suite { get; set; }

        /// <summary>
        /// Lista efectivă de destinatari: Recipients, iar dacă e goală și clientul
        /// a trimis formatul vechi, destinatarul unic din RecipientId.
        /// </summary>
        public IReadOnlyCollection<RecipientKeyInput> ResolveRecipients()
        {
            if (Recipients.Count > 0) return Recipients;

            if (Guid.TryParse(RecipientId, out var legacyId) && !string.IsNullOrWhiteSpace(EncryptedKeyForRecipient))
                return [new RecipientKeyInput { UserId = legacyId, EncryptedKeyForUser = EncryptedKeyForRecipient }];

            return [];
        }
    }

    /// <summary>Un destinatar și DEK-ul transferului împachetat cu cheia lui publică RSA-OAEP.</summary>
    public class RecipientKeyInput
    {
        public Guid UserId { get; set; }
        public string EncryptedKeyForUser { get; set; } = string.Empty;
    }

    public class ForwardTransferRequest
    {
        public List<RecipientKeyInput> Recipients { get; set; } = [];
    }

    public class ConfirmTransferDto
    {
        public bool? SignatureValid { get; set; }
    }

    public class RevokeTransferDto
    {
        public string? Reason { get; set; }
    }
}
