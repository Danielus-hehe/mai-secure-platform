using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;

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
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class TransfersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly ILogger<TransfersController> _logger;

        public TransfersController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            ILogger<TransfersController> logger)
        {
            _context        = context;
            _storage        = storage;
            _storageOptions = storageOptions;
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
        private const int MaxIvChars         = 32;

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
                ExpiresAt           = t.ExpiresAt,
                IsMine              = t.SenderId == userId,
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
        [RequestSizeLimit(52_428_800)]  // 50 MB; trebuie ținut sincron cu Storage:MaxFileSizeMb
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

            // ── Verificarea părților ─────────────────────────────────────────

            var recipient = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == recipientGuid, ct);

            if (recipient is null)
                return BadRequest(new { message = "Destinatarul nu a fost găsit." });

            if (!recipient.IsActive)
                return BadRequest(new { message = "Destinatarul are contul dezactivat." });

            // Fără cheie publică nu se poate împacheta nimic pentru el. Clientul ar
            // fi trebuit să prindă asta mai devreme, dar serverul nu se bazează
            // niciodată pe validarea făcută de client.
            if (string.IsNullOrEmpty(recipient.PublicKeyEncryption))
                return BadRequest(new
                {
                    message = $"Utilizatorul @{recipient.Username} nu și-a generat încă cheile. " +
                              "Nu i se pot trimite fișiere criptate."
                });

            var senderHasKeys = await _context.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == senderId && u.PublicKeyEncryption != null, ct);

            if (!senderHasKeys)
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
            var storageKey = BuildStorageKey(transferId);

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
                ExpiresAt                = DateTime.UtcNow.AddDays(30),
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
                Details   = $"SUCCES: Fisier criptat trimis '{safeName}' ({request.File.Length} octeti) " +
                            $"catre {recipient.FullName ?? recipient.Username}, suita {request.Suite}",
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

            return Ok(new
            {
                id          = transfer.Id,
                sha256      = computedHash,
                expiresAt   = transfer.ExpiresAt,
                message     = $"Fișierul '{safeName}' a fost trimis criptat.",
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
        /// </summary>
        [HttpPatch("{id:guid}/confirm")]
        public async Task<IActionResult> Confirm(
            Guid id, [FromBody] ConfirmTransferDto? dto, CancellationToken ct)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.RecipientId != userId) return Forbid();

            var signatureValid = dto?.SignatureValid ?? true;

            if (transfer.Status == TransferStatus.Pending)
            {
                transfer.Status       = TransferStatus.Downloaded;
                transfer.DownloadedAt = DateTime.UtcNow;
            }

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDownload,
                Details   = signatureValid
                    ? $"SUCCES: Fisier descarcat si decriptat '{transfer.FileName}', semnatura expeditorului VALIDA"
                    : $"ATENTIE: Fisier descarcat '{transfer.FileName}', semnatura expeditorului INVALIDA",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);

            return Ok(new { message = "Transfer confirmat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/Transfers/{id}
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Șterge rândul și obiectul din depozit. Fără asta, depozitul crește
        /// monoton: implementarea veche nu ștergea niciodată nimic.
        /// </summary>
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
                    // Rândul se șterge oricum: un obiect orfan în depozit expiră
                    // prin lifecycle policy, dar un rând orfan în bază rămâne
                    // vizibil în interfață și induce în eroare utilizatorul.
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
                Details   = $"SUCCES: Transfer sters '{transfer.FileName}' (id {transfer.Id})",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);

            return Ok(new { message = "Transferul a fost șters." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cheie ierarhică pe an și lună. Nu conține nimic derivat din datele
        /// utilizatorului: numele fișierului în cheie ar scurge informație către
        /// oricine vede listarea bucketului.
        /// </summary>
        private string BuildStorageKey(Guid transferId)
        {
            var now = DateTime.UtcNow;
            return $"{_storageOptions.TransfersPrefix}/{now:yyyy}/{now:MM}/{transferId:N}.enc";
        }

        /// <summary>
        /// SHA-256 al stream-ului, calculat incremental. Nu se încarcă tot
        /// conținutul în memorie — un fișier de 50 MB ar însemna 50 MB de heap
        /// per upload simultan.
        /// </summary>
        private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken ct)
        {
            using var sha    = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer       = new byte[81_920];
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                sha.AppendData(buffer, 0, read);

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>
        /// Validare structurală a plicului. Serverul nu poate verifica criptografic
        /// nimic (nu are cheile), dar poate refuza valori evident greșite înainte
        /// să scrie ceva în depozit.
        /// </summary>
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

    /// <summary>
    /// Corpul multipart al unui upload.
    ///
    /// Clasa asta lipsea complet din proiect, deși TransfersController o folosea:
    /// soluția nu compila.
    /// </summary>
    public class UploadTransferRequest
    {
        /// <summary>Cifrotextul. Conținutul în clar nu ajunge niciodată aici.</summary>
        public IFormFile? File { get; set; }

        public string? RecipientId { get; set; }

        /// <summary>Numele original al fișierului, trimis separat de numele blobului.</summary>
        public string? FileName { get; set; }

        /// <summary>Dimensiunea conținutului în clar, informativă.</summary>
        public long PlaintextSize { get; set; }

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
        /// <summary>
        /// Rezultatul verificării semnăturii în browser. Se jurnalizează ca atare:
        /// serverul nu poate verifica el însuși, dar poate consemna ce a raportat
        /// clientul, iar o valoare falsă în jurnal este exact genul de eveniment
        /// pe care un ofițer de securitate trebuie să-l vadă.
        /// </summary>
        public bool SignatureValid { get; set; } = true;
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
        public DateTime? ExpiresAt { get; set; }
        public bool IsMine { get; set; }
        public bool IsEncrypted { get; set; }
        public string? CryptoSuite { get; set; }
    }
}