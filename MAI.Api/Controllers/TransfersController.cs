using MAI.Api.Security;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage;
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
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class TransfersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly IEmailService _email;
        private readonly ILogger<TransfersController> _logger;

        public TransfersController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            IEmailService email,
            ILogger<TransfersController> logger)
        {
            _context        = context;
            _storage        = storage;
            _storageOptions = storageOptions;
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

        private const int MaxWrappedKeyChars  = 600;
        private const int MaxRevokeReasonChars = 256;
        private const int MaxIvChars           = 32;
        private const int MaxForwardRecipients = 20;

        private static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(7);

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Transfers
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] string? direction,
            [FromQuery] string sortBy = "createdAt",
            [FromQuery] string sortDir = "desc",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken ct = default)
        {
            var userId     = CurrentUserId;
            var pagination = new PaginationQuery { Page = page, PageSize = pageSize };

            // Includ transferurile unde utilizatorul este expeditor, destinatar
            // original sau destinatar adăugat ulterior prin forward.
            var query = _context.FileTransfers
                .AsNoTracking()
                .Include(t => t.Sender)
                .Include(t => t.Recipient)
                .Where(t => t.SenderId == userId
                         || t.RecipientId == userId
                         || t.Recipients.Any(r => r.UserId == userId));

            if (string.Equals(direction, "sent", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.SenderId == userId);
            else if (string.Equals(direction, "received", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t =>
                    t.RecipientId == userId || t.Recipients.Any(r => r.UserId == userId));

            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<TransferStatus>(status, ignoreCase: true, out var statusEnum))
                query = query.Where(t => t.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                query = query.Where(t =>
                    EF.Functions.ILike(t.FileName, term) ||
                    (t.Sender != null && (
                        EF.Functions.ILike(t.Sender.Username, term) ||
                        (t.Sender.FullName != null && EF.Functions.ILike(t.Sender.FullName, term)))) ||
                    (t.Recipient != null && (
                        EF.Functions.ILike(t.Recipient.Username, term) ||
                        (t.Recipient.FullName != null && EF.Functions.ILike(t.Recipient.FullName, term)))));
            }

            var total = await query.CountAsync(ct);
            query     = ApplySort(query, sortBy, sortDir);

            var raw = await query
                .Skip(pagination.Skip)
                .Take(pagination.PageSize)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;

            var items = raw.Select(t => new TransferDto
            {
                Id                  = t.Id,
                FileName            = t.FileName,
                FileSize            = t.FileSize,
                CiphertextSize      = t.CiphertextSize,
                Sha256              = t.ChecksumSHA256,
                SenderId            = t.SenderId,
                SenderName          = t.Sender?.FullName     ?? t.Sender?.Username    ?? "—",
                SenderDepartment    = t.Sender?.Department    ?? string.Empty,
                RecipientId         = t.RecipientId,
                RecipientName       = t.Recipient?.FullName   ?? t.Recipient?.Username ?? "—",
                RecipientDepartment = t.Recipient?.Department  ?? string.Empty,
                Status              = t.ExpiresAt.HasValue && t.ExpiresAt.Value < now
                                        && t.Status == TransferStatus.Pending
                                      ? nameof(TransferStatus.Expired)
                                      : t.Status.ToString(),
                CreatedAt           = t.CreatedAt,
                DownloadedAt        = t.DownloadedAt,
                SignatureValid      = t.RecipientSignatureValid,
                RevokedAt           = t.RevokedAt,
                RevokedReason       = t.RevokedReason,
                ExpiresAt           = t.ExpiresAt,
                Category            = t.Category,
                IsMine              = t.SenderId == userId,
                CanRevoke           = t.SenderId == userId
                                      && t.Status == TransferStatus.Pending
                                      && (!t.ExpiresAt.HasValue || t.ExpiresAt.Value > now),
                IsEncrypted         = t.IsEncrypted,
                CryptoSuite         = t.CryptoSuite,
            }).ToList();

            return Ok(PagedResult<TransferDto>.Create(items, total, pagination));
        }

        private static IQueryable<FileTransfer> ApplySort(
            IQueryable<FileTransfer> query, string sortBy, string sortDir)
        {
            var asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
            return sortBy?.ToLowerInvariant() switch
            {
                "filename" => asc ? query.OrderBy(t => t.FileName)  : query.OrderByDescending(t => t.FileName),
                "filesize" => asc ? query.OrderBy(t => t.FileSize)  : query.OrderByDescending(t => t.FileSize),
                "status"   => asc ? query.OrderBy(t => t.Status)    : query.OrderByDescending(t => t.Status),
                _          => asc ? query.OrderBy(t => t.CreatedAt) : query.OrderByDescending(t => t.CreatedAt),
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Transfers
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]
        public async Task<IActionResult> Upload(
            [FromForm] UploadTransferRequest request,
            CancellationToken ct)
        {
            var senderId = CurrentUserId;

            if (request.File is null || request.File.Length == 0)
                return BadRequest(new { message = "Cifrotextul este obligatoriu." });

            if (request.File.Length > _storageOptions.MaxFileSizeBytes)
                return BadRequest(new { message = $"Fișierul depășește limita de {_storageOptions.MaxFileSizeMb} MB." });

            if (!Guid.TryParse(request.RecipientId, out var recipientGuid))
                return BadRequest(new { message = "recipientId invalid." });

            if (senderId == recipientGuid)
                return BadRequest(new { message = "Nu poți trimite un fișier ție însuți." });

            if (!TryValidateEnvelope(request, out var envelopeError))
                return BadRequest(new { message = envelopeError });

            DateTime expiresAt;
            if (request.ExpiresAt.HasValue)
            {
                expiresAt = request.ExpiresAt.Value.ToUniversalTime();
                if (expiresAt <= DateTime.UtcNow)
                    return BadRequest(new { message = "Data de expirare nu poate fi în trecut." });
            }
            else
            {
                expiresAt = DateTime.UtcNow.Add(DefaultExpiry);
            }

            var recipient = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == recipientGuid, ct);

            if (recipient is null)
                return BadRequest(new { message = "Destinatarul nu a fost găsit." });
            if (!recipient.IsActive)
                return BadRequest(new { message = "Destinatarul are contul dezactivat." });
            if (string.IsNullOrEmpty(recipient.PublicKeyEncryption))
                return BadRequest(new
                {
                    message = $"Utilizatorul @{recipient.Username} nu și-a generat încă cheile. " +
                              "Nu i se pot trimite fișiere criptate."
                });

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
                Id                       = transferId,
                SenderId                 = senderId,
                RecipientId              = recipientGuid,
                FileName                 = safeName,
                StorageKey               = storageKey,
                FileSize                 = request.PlaintextSize > 0 ? request.PlaintextSize : request.File.Length,
                CiphertextSize           = request.File.Length,
                ChecksumSHA256           = computedHash,
                Status                   = TransferStatus.Pending,
                CreatedAt                = DateTime.UtcNow,
                ExpiresAt                = expiresAt,
                Category                 = request.Category,
                EncryptionIv             = request.Iv,
                EncryptedKeyForRecipient = request.EncryptedKeyForRecipient,
                EncryptedKeyForSender    = request.EncryptedKeyForSender,
                SenderSignature          = request.Signature,
                CryptoSuite              = request.Suite,
                IsEncrypted              = true,
            };

            _context.FileTransfers.Add(transfer);
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = senderId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileUpload,
                Details   = $"Fisier criptat trimis '{safeName}' ({request.File.Length} octeti) " +
                            $"catre {recipient.FullName ?? recipient.Username}, suita {request.Suite}, " +
                            $"categorie {request.Category}, expira {expiresAt:yyyy-MM-dd}, " +
                            $"cheie depozit: {storageKey}",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
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

            _ = _email.SendTransferNotificationAsync(
                    toEmail:    recipient.Email,
                    toName:     recipient.FullName ?? recipient.Username,
                    senderName: CurrentUsername,
                    fileName:   safeName,
                    expiresAt:  transfer.ExpiresAt,
                    ct:         CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logger.LogWarning(t.Exception,
                            "Notificarea email pentru transferul {Id} nu a putut fi trimisă.", transfer.Id);
                }, TaskScheduler.Default);

            return Ok(new
            {
                id        = transfer.Id,
                sha256    = computedHash,
                expiresAt = transfer.ExpiresAt,
                category  = transfer.Category.ToString(),
                message   = $"Fișierul '{safeName}' a fost trimis criptat.",
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
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            var isRecipient = transfer.RecipientId == userId;
            var isSender    = transfer.SenderId    == userId;

            // Dacă nu e nici expeditor, nici destinatar original, verificăm
            // dacă e destinatar adăugat prin forward.
            TransferRecipient? forwardEntry = null;
            if (!isRecipient && !isSender)
            {
                forwardEntry = await _context.TransferRecipients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.TransferId == id && r.UserId == userId, ct);

                if (forwardEntry is null)
                    return Forbid();
            }

            if (transfer.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message   = "Transferul a fost retras de expeditor și nu mai poate fi descărcat.",
                    revokedAt = transfer.RevokedAt,
                    reason    = transfer.RevokedReason,
                });

            if (transfer.ExpiresAt.HasValue && transfer.ExpiresAt.Value < DateTime.UtcNow)
                return StatusCode(410, new { message = "Transferul a expirat și nu mai poate fi descărcat." });

            if (!transfer.IsEncrypted)
                return Ok(new
                {
                    id          = transfer.Id,
                    fileName    = transfer.FileName,
                    isEncrypted = false,
                    downloadUrl = (string?)null,
                    message     = "Transfer necriptat, dinaintea migrării la criptare end-to-end.",
                });

            // Fiecare parte primește cheia împachetată PENTRU EA, nu pentru celălalt.
            var wrappedKeyForMe = forwardEntry is not null
                ? forwardEntry.EncryptedKeyForUser
                : isRecipient
                    ? transfer.EncryptedKeyForRecipient
                    : transfer.EncryptedKeyForSender;

            if (string.IsNullOrEmpty(wrappedKeyForMe))
                return StatusCode(409, new
                {
                    message = "Pentru acest transfer nu există o cheie împachetată pentru contul dumneavoastră."
                });

            string? downloadUrl = null;
            if (_storageOptions.UsePresignedDownload && _storage.SupportsPresignedUrls)
                downloadUrl = await _storage.TryCreatePresignedDownloadUrlAsync(
                    transfer.StorageKey, _storageOptions.PresignedUrlLifetime, ct);

            return Ok(new
            {
                id                      = transfer.Id,
                fileName                = transfer.FileName,
                fileSize                = transfer.FileSize,
                ciphertextSize          = transfer.CiphertextSize,
                isEncrypted             = true,
                iv                      = transfer.EncryptionIv,
                wrappedKeyForMe,
                signature               = transfer.SenderSignature,
                ciphertextSha256        = transfer.ChecksumSHA256,
                suite                   = transfer.CryptoSuite,
                senderId                = transfer.SenderId,
                senderName              = transfer.Sender?.FullName ?? transfer.Sender?.Username ?? "—",
                senderPublicKeySigning  = transfer.Sender?.PublicKeySigning,
                downloadUrl,
                expiresAt               = transfer.ExpiresAt,
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
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            // Verificăm accesul: expeditor, destinatar original sau forward.
            var isForwarded = transfer.SenderId  != userId
                           && transfer.RecipientId != userId
                           && await _context.TransferRecipients
                                  .AnyAsync(r => r.TransferId == id && r.UserId == userId, ct);

            if (transfer.SenderId != userId && transfer.RecipientId != userId && !isForwarded)
                return Forbid();

            if (transfer.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone, new { message = "Transferul a fost retras de expeditor." });

            if (transfer.ExpiresAt.HasValue && transfer.ExpiresAt.Value < DateTime.UtcNow)
                return StatusCode(410, new { message = "Transferul a expirat." });

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
        // POST api/Transfers/{id}/forward — redistribuie transferul
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Adaugă destinatari suplimentari la un transfer existent.
        ///
        /// Serverul nu atinge conținutul: clientul decriptează DEK-ul cu cheia
        /// lui privată, apoi îl re-împachetează cu cheia publică a fiecărui
        /// destinatar nou și trimite aici rezultatele. Garanția E2EE rămâne
        /// intactă — serverul primește doar niște blocuri RSA-OAEP opace.
        ///
        /// Cine poate face forward:
        ///   • Expeditorul original (are DEK împachetat în EncryptedKeyForSender)
        ///   • Destinatarul original
        ///   • Orice destinatar adăugat anterior prin forward
        /// Toți acești utilizatori au deja accesul la DEK (au sau pot obține
        /// plicul) și orice restricție suplimentară ar fi circumventată de cel
        /// care a descărcat deja fișierul în clar.
        /// </summary>
        [HttpPost("{id:guid}/forward")]
        public async Task<IActionResult> Forward(
            Guid id,
            [FromBody] ForwardTransferRequest? request,
            CancellationToken ct)
        {
            if (request?.Recipients is not { Count: > 0 })
                return BadRequest(new { message = "Lista destinatarilor nu poate fi goală." });

            if (request.Recipients.Count > MaxForwardRecipients)
                return BadRequest(new { message = $"Maxim {MaxForwardRecipients} destinatari per cerere." });

            var userId = CurrentUserId;

            // ── Transferul există și nu e retras/expirat ─────────────────────
            var transfer = await _context.FileTransfers
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            if (transfer.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone,
                    new { message = "Transferul a fost retras și nu mai poate fi redistribuit." });

            if (transfer.ExpiresAt.HasValue && transfer.ExpiresAt.Value < DateTime.UtcNow)
                return StatusCode(410,
                    new { message = "Transferul a expirat și nu mai poate fi redistribuit." });

            // ── Autorizare ────────────────────────────────────────────────────
            var isSender          = transfer.SenderId    == userId;
            var isOriginalRecipient = transfer.RecipientId == userId;
            var isForwardRecipient = !isSender && !isOriginalRecipient
                && await _context.TransferRecipients
                       .AnyAsync(r => r.TransferId == id && r.UserId == userId, ct);

            if (!isSender && !isOriginalRecipient && !isForwardRecipient)
                return Forbid();

            // ── Validarea fiecărui destinatar ─────────────────────────────────
            // Colectăm toate ID-urile ca să facem un singur SELECT, nu N SELECT-uri.
            var incomingIds = request.Recipients
                .Select(r => r.UserId)
                .Distinct()
                .ToList();

            if (incomingIds.Count != request.Recipients.Count)
                return BadRequest(new { message = "Lista conține destinatari duplicați." });

            // Nu poți face forward ție însuți.
            if (incomingIds.Contains(userId))
                return BadRequest(new { message = "Nu poți redirecționa un fișier către tine însuți." });

            // Utilizatori valizi: activi și cu chei generate.
            var validUsers = await _context.Users
                .AsNoTracking()
                .Where(u => incomingIds.Contains(u.Id) && u.IsActive && u.PublicKeyEncryption != null)
                .Select(u => new { u.Id, u.Username, u.FullName, u.Email })
                .ToListAsync(ct);

            if (validUsers.Count != incomingIds.Count)
            {
                var missingIds = incomingIds.Except(validUsers.Select(u => u.Id)).ToList();
                return BadRequest(new
                {
                    message = "Unul sau mai mulți destinatari nu există, sunt inactivi sau nu și-au generat cheile.",
                    invalidIds = missingIds,
                });
            }

            // Destinatari deja existenți (original sau forward): evităm duplicate.
            var alreadyRecipient = incomingIds.Where(uid => uid == transfer.RecipientId).ToList();

            var alreadyForwarded = await _context.TransferRecipients
                .AsNoTracking()
                .Where(r => r.TransferId == id && incomingIds.Contains(r.UserId))
                .Select(r => r.UserId)
                .ToListAsync(ct);

            var allAlready = alreadyRecipient.Union(alreadyForwarded).ToList();

            if (allAlready.Count > 0)
                return Conflict(new
                {
                    message = "Unul sau mai mulți utilizatori sunt deja destinatari ai acestui transfer.",
                    duplicateIds = allAlready,
                });

            // ── Validare plicuri criptografice ────────────────────────────────
            foreach (var r in request.Recipients)
            {
                if (string.IsNullOrWhiteSpace(r.EncryptedKeyForUser))
                    return BadRequest(new { message = $"Cheia pentru {r.UserId} lipsește." });

                if (r.EncryptedKeyForUser.Length > MaxWrappedKeyChars)
                    return BadRequest(new { message = $"Cheia pentru {r.UserId} depășește dimensiunea așteptată." });

                Span<byte> probe = new byte[r.EncryptedKeyForUser.Length];
                if (!Convert.TryFromBase64String(r.EncryptedKeyForUser, probe, out _))
                    return BadRequest(new { message = $"Cheia pentru {r.UserId} nu este base64 valid." });
            }

            // ── Salvare ───────────────────────────────────────────────────────
            var now      = DateTime.UtcNow;
            var userMap  = validUsers.ToDictionary(u => u.Id);

            var newRecipients = request.Recipients
                .Select(r => new TransferRecipient
                {
                    TransferId          = id,
                    UserId              = r.UserId,
                    EncryptedKeyForUser = r.EncryptedKeyForUser,
                    ForwardedById       = userId,
                    SentAt              = now,
                })
                .ToList();

            _context.TransferRecipients.AddRange(newRecipients);

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileUpload,   // refolosim FileUpload ca cel mai apropiat
                Details   = $"Forward transfer '{transfer.FileName}' (id {transfer.Id}) " +
                            $"catre {string.Join(", ", validUsers.Select(u => u.FullName ?? u.Username))}",
                IpAddress = CallerIp,
                Timestamp = now,
            });

            await _context.SaveChangesAsync(ct);

            // ── Notificări email ──────────────────────────────────────────────
            foreach (var user in validUsers)
            {
                _ = _email.SendTransferNotificationAsync(
                        toEmail:    user.Email,
                        toName:     user.FullName ?? user.Username,
                        senderName: CurrentUsername,
                        fileName:   transfer.FileName,
                        expiresAt:  transfer.ExpiresAt,
                        ct:         CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            _logger.LogWarning(t.Exception,
                                "Email forward pentru {Id} → {User} nu a putut fi trimis.",
                                transfer.Id, user.Username);
                    }, TaskScheduler.Default);
            }

            return Ok(new
            {
                message    = $"Transferul a fost redirecționat către {validUsers.Count} destinatar(i).",
                recipients = validUsers.Select(u => new
                {
                    id       = u.Id,
                    name     = u.FullName ?? u.Username,
                }),
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Transfers/{id}/confirm
        // ═════════════════════════════════════════════════════════════════════
        [HttpPatch("{id:guid}/confirm")]
        public async Task<IActionResult> Confirm(
            Guid id, [FromBody] ConfirmTransferDto? dto, CancellationToken ct)
        {
            if (dto?.SignatureValid is not bool signatureValid)
                return BadRequest(new { message = "Rezultatul verificării semnăturii (signatureValid) este obligatoriu." });

            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers.FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });

            // Confirmarea o poate face destinatarul original sau un destinatar de forward.
            var isOriginalRecipient = transfer.RecipientId == userId;
            var isForwardRecipient  = !isOriginalRecipient
                && await _context.TransferRecipients
                       .AnyAsync(r => r.TransferId == id && r.UserId == userId, ct);

            if (!isOriginalRecipient && !isForwardRecipient)
                return Forbid();

            if (transfer.Status == TransferStatus.Downloaded)
                return Ok(new
                {
                    message          = "Primirea era deja confirmată.",
                    alreadyConfirmed = true,
                    downloadedAt     = transfer.DownloadedAt,
                    signatureValid   = transfer.RecipientSignatureValid,
                });

            if (transfer.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a fost retras de expeditor. Primirea nu se mai înregistrează.",
                });

            if (transfer.Status != TransferStatus.Pending)
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a expirat. Primirea nu se mai înregistrează.",
                });

            transfer.Status                  = TransferStatus.Downloaded;
            transfer.DownloadedAt            = DateTime.UtcNow;
            transfer.RecipientSignatureValid = signatureValid;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDownload,
                Details   = signatureValid
                    ? $"Fisier descarcat si decriptat '{transfer.FileName}', semnatura expeditorului VALIDA"
                    : $"Fisier descarcat '{transfer.FileName}', semnatura expeditorului INVALIDA",
                Result    = signatureValid ? AuditResult.Success : AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Transfer confirmat.", alreadyConfirmed = false });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Transfers/{id}/revoke
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost("{id:guid}/revoke")]
        public async Task<IActionResult> Revoke(
            Guid id, [FromBody] RevokeTransferDto? dto, CancellationToken ct)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers.FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.SenderId != userId)
                return Forbid();

            if (transfer.Status == TransferStatus.Downloaded)
                return Conflict(new
                {
                    message = "Transferul a fost deja descărcat și nu mai poate fi retras. " +
                              "Fișierul se află pe dispozitivul destinatarului.",
                    downloadedAt = transfer.DownloadedAt,
                });

            if (transfer.Status != TransferStatus.Pending)
                return Conflict(new { message = "Transferul nu mai este în așteptare." });

            if (!string.IsNullOrEmpty(transfer.StorageKey))
            {
                try
                {
                    await _storage.DeleteAsync(transfer.StorageKey, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Retragere esuata: obiectul {Key} nu a putut fi sters.", transfer.StorageKey);
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId    = userId,
                        Username  = CurrentUsername,
                        Action    = AuditAction.TransferRevoked,
                        Details   = $"Retragere esuata pentru '{transfer.FileName}': obiectul nu a putut fi sters",
                        Result    = AuditResult.Failure,
                        IpAddress = CallerIp,
                        Timestamp = DateTime.UtcNow,
                    });
                    await _context.SaveChangesAsync(ct);
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        message = "Fișierul nu a putut fi șters din depozit. Transferul NU a fost retras. Încercați din nou.",
                    });
                }
            }

            transfer.Status        = TransferStatus.Revoked;
            transfer.RevokedAt     = DateTime.UtcNow;
            transfer.RevokedReason = Truncate(dto?.Reason, MaxRevokeReasonChars);
            transfer.EncryptedKeyForRecipient = null;
            transfer.EncryptedKeyForSender    = null;
            transfer.StorageKey               = string.Empty;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.TransferRevoked,
                Details   = string.IsNullOrWhiteSpace(transfer.RevokedReason)
                    ? $"Transfer retras '{transfer.FileName}' inainte de descarcare"
                    : $"Transfer retras '{transfer.FileName}' inainte de descarcare: {transfer.RevokedReason}",
                Result    = AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Transferul a fost retras. Fișierul nu mai poate fi descărcat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/Transfers/{id}
        // ═════════════════════════════════════════════════════════════════════
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers.FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.SenderId != userId && !IsAdministrator) return Forbid();

            if (!string.IsNullOrEmpty(transfer.StorageKey))
            {
                try { await _storage.DeleteAsync(transfer.StorageKey, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Obiectul {Key} nu a putut fi șters din depozit.", transfer.StorageKey);
                }
            }

            _context.FileTransfers.Remove(transfer);
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDeleted,
                Details   = $"Transfer sters '{transfer.FileName}' (id {transfer.Id})",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Transferul a fost șters." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim() is var v && v.Length <= max ? v : value.Trim()[..max];

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
            if (string.IsNullOrWhiteSpace(r.Iv) || string.IsNullOrWhiteSpace(r.EncryptedKeyForRecipient) ||
                string.IsNullOrWhiteSpace(r.EncryptedKeyForSender) || string.IsNullOrWhiteSpace(r.Signature) ||
                string.IsNullOrWhiteSpace(r.CiphertextSha256))
            {
                error = "Plic criptografic incomplet."; return false;
            }
            if (r.Iv.Length > MaxIvChars || r.EncryptedKeyForRecipient.Length > MaxWrappedKeyChars ||
                r.EncryptedKeyForSender.Length > MaxWrappedKeyChars || r.Signature.Length > MaxWrappedKeyChars)
            {
                error = "Câmpurile plicului depășesc dimensiunile așteptate."; return false;
            }
            if (r.CiphertextSha256.Length != 64 || !r.CiphertextSha256.All(Uri.IsHexDigit))
            {
                error = "Amprenta SHA-256 trebuie să fie 64 de caractere hexazecimale."; return false;
            }
            foreach (var (name, value) in new[]
            {
                ("iv", r.Iv), ("encryptedKeyForRecipient", r.EncryptedKeyForRecipient),
                ("encryptedKeyForSender", r.EncryptedKeyForSender), ("signature", r.Signature),
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
    // Modele request / response
    // ═════════════════════════════════════════════════════════════════════════

    public class UploadTransferRequest
    {
        public IFormFile? File { get; set; }
        public string? RecipientId { get; set; }
        public string? FileName { get; set; }
        public long PlaintextSize { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public TransferCategory Category { get; set; } = TransferCategory.General;
        public string? Iv { get; set; }
        public string? EncryptedKeyForRecipient { get; set; }
        public string? EncryptedKeyForSender { get; set; }
        public string? Signature { get; set; }
        public string? CiphertextSha256 { get; set; }
        public string? Suite { get; set; }
    }

    public class ForwardTransferRequest
    {
        public List<ForwardRecipientInput> Recipients { get; set; } = [];
    }

    public class ForwardRecipientInput
    {
        /// <summary>Id-ul utilizatorului destinatar.</summary>
        public Guid UserId { get; set; }

        /// <summary>DEK-ul transferului împachetat cu cheia publică RSA-OAEP a acestui utilizator.</summary>
        public string EncryptedKeyForUser { get; set; } = string.Empty;
    }

    public class ConfirmTransferDto
    {
        public bool? SignatureValid { get; set; }
    }

    public class RevokeTransferDto
    {
        public string? Reason { get; set; }
    }

    public class TransferDto
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public long CiphertextSize { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string SenderDepartment { get; set; } = string.Empty;
        public Guid RecipientId { get; set; }
        public string RecipientName { get; set; } = string.Empty;
        public string RecipientDepartment { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? DownloadedAt { get; set; }
        public bool? SignatureValid { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevokedReason { get; set; }
        public bool CanRevoke { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public TransferCategory Category { get; set; }
        public bool IsMine { get; set; }
        public bool IsEncrypted { get; set; }
        public string? CryptoSuite { get; set; }
    }
}
