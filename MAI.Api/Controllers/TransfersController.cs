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
    /// <summary>
    /// Transferuri securizate de fișiere între angajați.
    ///
    /// Serverul nu vede niciodată conținutul. Clientul criptează fișierul cu o
    /// cheie AES-256-GCM aleatorie, împachetează cheia cu cheile publice RSA ale
    /// destinatarului și ale expeditorului, semnează amprenta conținutului în
    /// clar cu RSA-PSS, apoi trimite aici doar cifrotextul și plicul.
    ///
    /// Rolul controllerului este strict: autorizare, validare structurală,
    /// stocarea octeților opaci și jurnalizare. Nicio operație criptografică
    /// asupra conținutului nu se face pe server — dacă s-ar face, întreaga
    /// garanție end-to-end ar dispărea.
    ///
    /// Cheile de obiect urmează structura:
    ///   {department}/{yyyy}/{MM}/{transferId:N}.enc
    /// Ex.: directia-it/2026/09/a1b2c3...enc
    ///
    /// Un singur bucket ("mai-secure"), prefixul de prim nivel este departamentul
    /// expeditorului, normalizat Unicode → ASCII slug. Transferurile din
    /// departamente diferite sunt izolate vizual și pot primi politici S3 diferite
    /// fără să fie mutate.
    /// </summary>
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

        // Lungimile maxime acceptate pentru câmpurile base64 ale plicului.
        // RSA-3072 produce blocuri de 384 de octeți → 512 caractere base64.
        private const int MaxWrappedKeyChars = 600;

        /// <summary>Motivul retragerii e text liber de la utilizator; se trunchiază.</summary>
        private const int MaxRevokeReasonChars = 256;
        private const int MaxIvChars           = 32;

        /// <summary>Durata implicită de valabilitate dacă expeditorul nu specifică alta.</summary>
        private static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(7);

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Transfers?search=&status=&direction=&sortBy=&sortDir=&page=&pageSize=
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

            var query = _context.FileTransfers
                .AsNoTracking()
                .Include(t => t.Sender)
                .Include(t => t.Recipient)
                .Where(t => t.SenderId == userId || t.RecipientId == userId);

            if (string.Equals(direction, "sent", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.SenderId == userId);
            else if (string.Equals(direction, "received", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.RecipientId == userId);

            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<TransferStatus>(status, ignoreCase: true, out var statusEnum))
            {
                query = query.Where(t => t.Status == statusEnum);
            }

            // Căutare server-side: ILike se traduce în ILIKE PostgreSQL, deci
            // filtrarea rămâne în baza de date.
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

            query = ApplySort(query, sortBy, sortDir);

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

        /// <summary>
        /// Sortare pe coloană. Lista de coloane permise este fixă — nu se construiește
        /// SQL din string-ul primit de la client.
        /// </summary>
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
        // POST api/Transfers — încarcă cifrotextul plus plicul criptografic
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Primește cifrotextul deja criptat în browser. Corpul cererii nu este
        /// citit niciodată în memorie: ASP.NET îl bufferizează pe disc temporar,
        /// iar de acolo trece în depozit ca stream.
        /// </summary>
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]  // 50 MB + antet multipart; vezi UploadLimits
        public async Task<IActionResult> Upload(
            [FromForm] UploadTransferRequest request,
            CancellationToken ct)
        {
            var senderId = CurrentUserId;

            // ── Validări structurale ─────────────────────────────────────────

            if (request.File is null || request.File.Length == 0)
                return BadRequest(new { message = "Cifrotextul este obligatoriu." });

            if (request.File.Length > _storageOptions.MaxFileSizeBytes)
                return BadRequest(new
                {
                    message = $"Fișierul depășește limita de {_storageOptions.MaxFileSizeMb} MB."
                });

            if (!Guid.TryParse(request.RecipientId, out var recipientGuid))
                return BadRequest(new { message = "recipientId invalid." });

            if (senderId == recipientGuid)
                return BadRequest(new { message = "Nu poți trimite un fișier ție însuți." });

            if (!TryValidateEnvelope(request, out var envelopeError))
                return BadRequest(new { message = envelopeError });

            // ExpiresAt: clientul poate trimite o dată; dacă lipsește se aplică
            // implicit 7 zile. Se impune că data să nu fie în trecut.
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

            // ── Verificarea destinatarului ───────────────────────────────────

            var recipient = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == recipientGuid, ct);

            if (recipient is null)
                return BadRequest(new { message = "Destinatarul nu a fost găsit." });

            if (!recipient.IsActive)
                return BadRequest(new { message = "Destinatarul are contul dezactivat." });

            // Fără cheie publică nu se poate împacheta nimic pentru el.
            if (string.IsNullOrEmpty(recipient.PublicKeyEncryption))
                return BadRequest(new
                {
                    message = $"Utilizatorul @{recipient.Username} nu și-a generat încă cheile. " +
                              "Nu i se pot trimite fișiere criptate."
                });

            // ── Expeditorul: chei + departament ─────────────────────────────
            //
            // Citim o proiecție minimă: PublicKeyEncryption (validare cheie) și
            // Department (construcția cheii de obiect). A doua cerere SQL față de
            // varianta anterioară care folosea .AnyAsync — prețul mic pentru a
            // putea organiza depozitul pe departamente fără coloane suplimentare.

            var senderInfo = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == senderId)
                .Select(u => new { u.PublicKeyEncryption, u.Department })
                .FirstOrDefaultAsync(ct);

            if (senderInfo?.PublicKeyEncryption is null)
                return BadRequest(new
                {
                    message = "Nu ai chei înregistrate. Generează-le înainte de a trimite fișiere."
                });

            // Path.GetFileName elimină componentele de cale din numele trimis de
            // client: un fileName de forma "../../appsettings.json" devine inofensiv.
            var safeName = Path.GetFileName(request.FileName ?? request.File.FileName);
            if (string.IsNullOrWhiteSpace(safeName))
                return BadRequest(new { message = "Numele fișierului este invalid." });
            if (safeName.Length > 260)
                safeName = safeName[..260];

            // ── Amprenta cifrotextului ───────────────────────────────────────
            // Se calculează pe server, peste octeții primiți efectiv, și se compară
            // cu cea declarată de client. Dacă nu coincid, ceva a alterat conținutul
            // pe drum și nu se stochează nimic.

            await using var upload = request.File.OpenReadStream();

            var computedHash = await ComputeSha256HexAsync(upload, ct);

            if (!string.Equals(computedHash, request.CiphertextSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Amprentă necorespunzătoare la upload de la {User}: declarat {Declared}, calculat {Computed}",
                    CurrentUsername, request.CiphertextSha256, computedHash);

                return BadRequest(new
                {
                    message = "Amprenta SHA-256 a cifrotextului nu corespunde. " +
                              "Fișierul a fost alterat în timpul transferului."
                });
            }

            if (!upload.CanSeek)
                return StatusCode(500, new { message = "Stream de upload nerepozitionabil." });

            upload.Position = 0;

            // ── Scriere în depozit, apoi în baza de date ─────────────────────

            var transferId = Guid.NewGuid();

            // Cheia include departamentul expeditorului ca prefix de prim nivel.
            // Format: {dept}/{yyyy}/{MM}/{transferId:N}.enc
            // Ex.:    directia-it/2026/09/a1b2c3def4....enc
            var storageKey = BuildStorageKey(transferId, senderInfo.Department);

            await _storage.PutAsync(
                storageKey, upload, request.File.Length, "application/octet-stream", ct);

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
                // Obiectul a ajuns în depozit dar rândul nu s-a salvat. Fără
                // compensare ar rămâne acolo pentru totdeauna, invizibil și
                // imposibil de șters din interfață.
                _logger.LogError(ex,
                    "Salvarea transferului a eșuat. Se retrage obiectul {Key} din depozit.", storageKey);

                try { await _storage.DeleteAsync(storageKey, CancellationToken.None); }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx, "Retragerea obiectului {Key} a eșuat.", storageKey);
                }

                throw;
            }

            // ── Notificare email ──────────────────────────────────────────────
            // Eșecul emailului nu afectează răspunsul: transferul e deja salvat
            // și cifrotextul e în depozit. Se loghează un warning, atât.
            _ = _email.SendTransferNotificationAsync(
                    toEmail:    recipient.Email,
                    toName:     recipient.FullName ?? recipient.Username,
                    senderName: CurrentUsername,
                    fileName:   safeName,
                    expiresAt:  transfer.ExpiresAt,
                    ct:         CancellationToken.None)   // nu anulăm odată cu cererea HTTP
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
        // GET api/Transfers/{id}/envelope — plicul + modul de descărcare
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Returnează metadatele criptografice de care are nevoie clientul ca să
        /// decripteze: IV-ul, cheia de fișier împachetată PENTRU EL (nu pentru
        /// celălalt) și cheia publică de semnătură a expeditorului.
        ///
        /// Autorizarea se face aici. Dacă providerul suportă, tot aici se semnează
        /// URL-ul temporar de descărcare directă din depozit — cine nu are dreptul
        /// nu primește niciodată un URL semnat.
        /// </summary>
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
            var isSender    = transfer.SenderId == userId;

            if (!isRecipient && !isSender)
                return Forbid();

            // Verificat explicit, înaintea expirării și a cheilor. Fără el, un
            // transfer retras ajungea la ramura „nu există cheie împachetată
            // pentru contul dumneavoastră" (retragerea golește cheile) — un 409
            // care îl trimite pe destinatar să caute o problemă de chei, nu să
            // afle că expeditorul a retras documentul.
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
            {
                return Ok(new
                {
                    id          = transfer.Id,
                    fileName    = transfer.FileName,
                    isEncrypted = false,
                    downloadUrl = (string?)null,
                    message     = "Transfer necriptat, dinaintea migrării la criptare end-to-end.",
                });
            }

            // Fiecare parte primește DOAR plicul deschis cu cheia ei.
            var wrappedKeyForMe = isRecipient
                ? transfer.EncryptedKeyForRecipient
                : transfer.EncryptedKeyForSender;

            if (string.IsNullOrEmpty(wrappedKeyForMe))
                return StatusCode(409, new
                {
                    message = "Pentru acest transfer nu există o cheie împachetată pentru contul dumneavoastră."
                });

            string? downloadUrl = null;
            if (_storageOptions.UsePresignedDownload && _storage.SupportsPresignedUrls)
            {
                downloadUrl = await _storage.TryCreatePresignedDownloadUrlAsync(
                    transfer.StorageKey, _storageOptions.PresignedUrlLifetime, ct);
            }

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
        // GET api/Transfers/{id}/content — cifrotextul, prin API
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Variantă de rezervă pentru când URL-urile presemnate nu sunt disponibile
        /// (provider local) sau sunt dezactivate. Octeții trec prin API, ceea ce
        /// dublează traficul — de aceea nu e calea implicită.
        /// </summary>
        [HttpGet("{id:guid}/content")]
        public async Task<IActionResult> GetContent(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;

            var transfer = await _context.FileTransfers
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null)
                return NotFound(new { message = "Transferul nu a fost găsit." });

            if (transfer.SenderId != userId && transfer.RecipientId != userId)
                return Forbid();

            if (transfer.Status == TransferStatus.Revoked)
                return StatusCode(StatusCodes.Status410Gone,
                    new { message = "Transferul a fost retras de expeditor." });

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
                _logger.LogError(
                    "Rând de transfer fără obiect în depozit: {Id} → {Key}", transfer.Id, transfer.StorageKey);
                return NotFound(new { message = "Conținutul nu mai există în depozit." });
            }

            // Numele trimis e generic: fișierul real se salvează sub numele
            // original abia după decriptare, în browser.
            return File(stream, "application/octet-stream", $"{transfer.Id}.enc");
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/Transfers/{id}/confirm — marchează preluarea
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Apelat de client DUPĂ ce decriptarea și verificarea semnăturii au reușit.
        /// Momentul contează: dacă statusul s-ar seta la emiterea URL-ului, un
        /// transfer eșuat ar apărea în jurnal ca preluat cu succes.
        ///
        /// Trei reguli fac din confirmare o dovadă, nu doar un contor:
        ///   • Rezultatul semnăturii e obligatoriu. O cerere fără el nu mai e
        ///     înregistrată implicit ca „semnătură validă".
        ///   • Prima confirmare rămâne. O descărcare repetată primește 200, dar nu
        ///     rescrie rezultatul și nu adaugă alt rând în audit: altfel un
        ///     „INVALIDĂ" consemnat putea fi înlocuit ulterior cu „VALIDĂ".
        ///   • Transferurile retrase sau expirate nu se mai confirmă.
        /// </summary>
        [HttpPatch("{id:guid}/confirm")]
        public async Task<IActionResult> Confirm(
            Guid id, [FromBody] ConfirmTransferDto? dto, CancellationToken ct)
        {
            if (dto?.SignatureValid is not bool signatureValid)
            {
                return BadRequest(new
                {
                    message = "Rezultatul verificării semnăturii (signatureValid) este obligatoriu.",
                });
            }

            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.RecipientId != userId) return Forbid();

            if (transfer.Status == TransferStatus.Downloaded)
            {
                return Ok(new
                {
                    message          = "Primirea era deja confirmată.",
                    alreadyConfirmed = true,
                    downloadedAt     = transfer.DownloadedAt,
                    signatureValid   = transfer.RecipientSignatureValid,
                });
            }

            if (transfer.Status == TransferStatus.Revoked)
            {
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a fost retras de expeditor. Primirea nu se mai înregistrează.",
                });
            }

            // Doar statusul Expired, pus de job, oprește confirmarea. Un transfer
            // încă Pending, dar trecut de ExpiresAt, se confirmă: plicul s-a putut
            // obține doar înainte de termen (GetEnvelope refuză după), deci
            // descărcarea a început la timp și primirea e reală.
            if (transfer.Status != TransferStatus.Pending)
            {
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    message = "Transferul a expirat. Primirea nu se mai înregistrează.",
                });
            }

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
            {
                return Conflict(new
                {
                    message = "Transferul a fost deja descărcat și nu mai poate fi retras. " +
                              "Fișierul se află pe dispozitivul destinatarului.",
                    downloadedAt = transfer.DownloadedAt,
                });
            }

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
                    _logger.LogError(ex,
                        "Retragere esuata: obiectul {Key} nu a putut fi sters.", transfer.StorageKey);

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
                        message = "Fișierul nu a putut fi șters din depozit. " +
                                  "Transferul NU a fost retras. Încercați din nou.",
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

            if (transfer.SenderId != userId && !IsAdministrator)
                return Forbid();

            if (!string.IsNullOrEmpty(transfer.StorageKey))
            {
                try
                {
                    await _storage.DeleteAsync(transfer.StorageKey, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Obiectul {Key} nu a putut fi șters din depozit.", transfer.StorageKey);
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

        /// <summary>
        /// Construiește cheia de obiect în depozit.
        ///
        /// Format: {dept-slug}/{yyyy}/{MM}/{transferId:N}.enc
        /// Ex.:    directia-it/2026/09/a1b2c3d4e5f6....enc
        ///
        /// Cheia e stocată în coloana StorageKey a rândului FileTransfer și nu se
        /// modifică după creare. Transferurile existente cu formatul vechi
        /// (transfers/{yyyy}/{MM}/...) continuă să fie citite corect — storage-ul
        /// folosește cheia din baza de date, nu o recalculează.
        /// </summary>
        private static string BuildStorageKey(Guid transferId, string? senderDepartment)
        {
            var now  = DateTime.UtcNow;
            var dept = SanitizeDepartment(senderDepartment);
            return $"{dept}/{now:yyyy}/{now:MM}/{transferId:N}.enc";
        }

        /// <summary>
        /// Transformă un șir arbitrar (denumire departament din AD sau din UI) într-un
        /// slug S3-safe, folosit ca prefix de prim nivel în cheile de obiect.
        ///
        /// Pași:
        ///   1. Normalizare NFKD — separă literele de diacriticele lor combinate:
        ///      „ș" → 's' + combining cedilla, „â" → 'a' + combining circumflex etc.
        ///   2. Filtrare: reținem doar caracterele non-diacritice ASCII-alfanumerice;
        ///      orice separator (spațiu, liniuță, underscore etc.) devine cratimă.
        ///   3. Deduplicare cratime consecutive + eliminare de la margini.
        ///   4. Trunchiere la 50 de caractere (cheile S3 au limita de 1024 bytes,
        ///      dar un prefix prea lung e greu de citit în consolă).
        ///   5. Fallback la "general" dacă rezultatul e gol.
        ///
        /// Exemple:
        ///   "Direcția Generală de Poliție" → "directia-generala-de-politie"
        ///   "IT & Securitate"              → "it-securitate"
        ///   null / ""                      → "general"
        /// </summary>
        private static string SanitizeDepartment(string? department)
        {
            if (string.IsNullOrWhiteSpace(department))
                return "general";

            // NFKD: literele compuse sunt descompuse în baza + diacritic combinat.
            var nfkd = department.Normalize(NormalizationForm.FormD);

            var sb         = new StringBuilder(nfkd.Length);
            var prevWasSep = true; // suprimă cratima de la început

            foreach (var ch in nfkd)
            {
                // NonSpacingMark = diacriticele combinate (cedilla, circumflex etc.)
                if (char.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsAsciiLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    prevWasSep = false;
                }
                else if (!prevWasSep)
                {
                    // Orice non-alfanumeric (spațiu, &, /, -, _, punct …) → o singură cratimă.
                    sb.Append('-');
                    prevWasSep = true;
                }
                // Dacă prevWasSep e deja true (separator precedent) ignorăm caracterul.
            }

            // Eliminăm cratima finală dacă ultimul caracter din string era separator.
            if (sb.Length > 0 && sb[^1] == '-')
                sb.Length--;

            var result = sb.ToString();

            if (string.IsNullOrEmpty(result))
                return "general";

            // Trunchiere: max 50 de caractere, fără a lăsa o cratimă la sfârșit.
            if (result.Length > 50)
                result = result[..50].TrimEnd('-');

            return string.IsNullOrEmpty(result) ? "general" : result;
        }

        private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken ct)
        {
            using var sha    = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer       = new byte[81_920];
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                sha.AppendData(buffer, 0, read);

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        private static bool TryValidateEnvelope(UploadTransferRequest r, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(r.Iv) ||
                string.IsNullOrWhiteSpace(r.EncryptedKeyForRecipient) ||
                string.IsNullOrWhiteSpace(r.EncryptedKeyForSender) ||
                string.IsNullOrWhiteSpace(r.Signature) ||
                string.IsNullOrWhiteSpace(r.CiphertextSha256))
            {
                error = "Plic criptografic incomplet.";
                return false;
            }

            if (r.Iv.Length > MaxIvChars ||
                r.EncryptedKeyForRecipient.Length > MaxWrappedKeyChars ||
                r.EncryptedKeyForSender.Length > MaxWrappedKeyChars ||
                r.Signature.Length > MaxWrappedKeyChars)
            {
                error = "Câmpurile plicului depășesc dimensiunile așteptate.";
                return false;
            }

            if (r.CiphertextSha256.Length != 64 ||
                !r.CiphertextSha256.All(Uri.IsHexDigit))
            {
                error = "Amprenta SHA-256 trebuie să fie 64 de caractere hexazecimale.";
                return false;
            }

            foreach (var (name, value) in new[]
            {
                ("iv", r.Iv),
                ("encryptedKeyForRecipient", r.EncryptedKeyForRecipient),
                ("encryptedKeyForSender", r.EncryptedKeyForSender),
                ("signature", r.Signature),
            })
            {
                Span<byte> probe = new byte[value.Length];
                if (!Convert.TryFromBase64String(value, probe, out _))
                {
                    error = $"Câmpul '{name}' nu este base64 valid.";
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(r.Suite) || r.Suite.Length > 128)
            {
                error = "Suita criptografică lipsește sau este invalidă.";
                return false;
            }

            return true;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Modele
    // ═════════════════════════════════════════════════════════════════════════

    public class UploadTransferRequest
    {
        /// <summary>Cifrotextul. Conținutul în clar nu ajunge niciodată aici.</summary>
        public IFormFile? File { get; set; }

        public string? RecipientId { get; set; }

        /// <summary>Numele original al fișierului, trimis separat de numele blobului.</summary>
        public string? FileName { get; set; }

        /// <summary>Dimensiunea conținutului în clar, informativă.</summary>
        public long PlaintextSize { get; set; }

        /// <summary>
        /// Dată de expirare aleasă de expeditor (UTC, opțional).
        /// Dacă lipsește, se aplică implicit 7 zile de la creare.
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Categoria transferului pentru filtrare și prioritizare.
        /// Valoarea implicită este General dacă clientul nu trimite altceva.
        /// </summary>
        public TransferCategory Category { get; set; } = TransferCategory.General;

        // ── Plicul criptografic ──────────────────────────────────────────────
        public string? Iv { get; set; }
        public string? EncryptedKeyForRecipient { get; set; }
        public string? EncryptedKeyForSender { get; set; }
        public string? Signature { get; set; }
        public string? CiphertextSha256 { get; set; }
        public string? Suite { get; set; }
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

        // ── Dovada de primire ────────────────────────────────────────────────

        public DateTime? DownloadedAt { get; set; }
        public bool? SignatureValid { get; set; }

        // ── Retragere ────────────────────────────────────────────────────────

        public DateTime? RevokedAt { get; set; }
        public string? RevokedReason { get; set; }
        public bool CanRevoke { get; set; }

        // ── Expirare și categorie ────────────────────────────────────────────

        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Categoria transferului. Serialized ca număr de către ASP.NET;
        /// frontul îl mapează la eticheta corespunzătoare.
        /// </summary>
        public TransferCategory Category { get; set; }

        public bool IsMine { get; set; }
        public bool IsEncrypted { get; set; }
        public string? CryptoSuite { get; set; }
    }
}
