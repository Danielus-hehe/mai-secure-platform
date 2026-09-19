using MAI.Api.Models;
using MAI.Api.Security;
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
    /// Registrul de documente normative, cu versionare.
    ///
    /// Spre deosebire de transferuri, documentele NU sunt criptate end-to-end:
    /// sunt acte interne cu circulație generală în instituție, iar căutarea
    /// server-side după titlu și cuvinte-cheie este cerută funcțional. Ce se
    /// schimbă față de versiunea anterioară este modul în care ajung pe disc și
    /// modul în care se citesc înapoi.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly ILogger<DocumentsController> _logger;

        /// <summary>Prefixul sub care stau obiectele documentelor în depozit.</summary>
        private const string DocumentsPrefix = "documents";

        public DocumentsController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            ILogger<DocumentsController> logger)
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

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Documents?search=
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? search, CancellationToken ct)
        {
            var query = _context.Documents
                .AsNoTracking()
                .Include(d => d.Versions)
                .Include(d => d.CreatedBy)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                // ILike se traduce în ILIKE PostgreSQL: filtrarea rămâne în baza
                // de date. ToLower().Contains() forța o conversie pe fiecare rând
                // și anula orice index.
                var term = $"%{search.Trim()}%";
                query = query.Where(d =>
                    EF.Functions.ILike(d.Title, term) ||
                    EF.Functions.ILike(d.DocumentNumber, term) ||
                    EF.Functions.ILike(d.Keywords, term));
            }

            var docs = await query
                .OrderByDescending(d => d.CreatedAt)
                .Take(200)
                .ToListAsync(ct);

            var result = docs.Select(d => new
            {
                id             = d.Id,
                title          = d.Title,
                number         = d.DocumentNumber,
                category       = d.Category,
                keywords       = d.Keywords
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToArray(),
                currentVersion = d.CurrentVersion,
                publishedBy    = d.CreatedBy?.FullName ?? d.CreatedBy?.Username ?? "-",
                publishedAt    = d.CreatedAt,
                versions       = d.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new
                    {
                        version = v.VersionNumber,
                        // Numele de afișare, nu calea. Versiunea anterioară scotea
                        // Path.GetFileName dintr-o cale ABSOLUTĂ de pe server; e o
                        // scurgere gratuită de topologie a mașinii către orice
                        // utilizator autentificat.
                        fileName    = v.OriginalFileNameOrKey(),
                        sha256      = v.ChecksumSHA256,
                        uploadedAt  = v.CreatedAt,
                        uploadedBy  = v.CreatedBy,
                        changeNotes = v.ChangeNotes,
                        isArchived  = v.VersionNumber < d.CurrentVersion,
                    })
                    .ToArray(),
            });

            return Ok(result);
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Documents - publică document nou
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "SefDirectie,Administrator")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]
        public async Task<IActionResult> Create(
            [FromForm] CreateDocumentRequest request, CancellationToken ct)
        {
            var title = request.Title?.Trim() ?? string.Empty;
            var file  = request.File;

            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { message = "Titlul este obligatoriu." });

            if (title.Length > 300)
                return BadRequest(new { message = "Titlul nu poate depăși 300 de caractere." });

            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Fișierul este obligatoriu." });

            if (file.Length > _storageOptions.MaxFileSizeBytes)
                return BadRequest(new
                {
                    message = $"Fișierul depășește limita de {_storageOptions.MaxFileSizeMb} MB.",
                });

            var docId      = Guid.NewGuid();
            var safeName   = SanitizeFileName(file.FileName);
            var storageKey = BuildStorageKey(docId, 1, safeName);

            var (checksum, stored) = await StoreAsync(file, storageKey, ct);
            if (!stored)
                return StatusCode(502, new { message = "Fișierul nu a putut fi scris în depozit." });

            var doc = new Document
            {
                Id             = docId,
                Title          = title,
                DocumentNumber = request.Number?.Trim() ?? "-",
                Category       = request.Category,
                Keywords       = request.Keywords?.Trim() ?? string.Empty,
                CurrentVersion = 1,
                CreatedById    = CurrentUserId,
                CreatedAt      = DateTime.UtcNow,
            };

            _context.Documents.Add(doc);
            _context.DocumentVersions.Add(new DocumentVersion
            {
                Id                   = Guid.NewGuid(),
                DocumentId           = doc.Id,
                VersionNumber        = 1,
                EncryptedStoragePath = storageKey,
                // Amprenta se calculează efectiv, nu se lasă goală. Fără ea,
                // coloana ChecksumSHA256 promitea o garanție de integritate pe
                // care nimic nu o producea: un act normativ modificat direct în
                // depozit nu ar fi fost detectabil în niciun fel.
                ChecksumSHA256       = checksum,
                CreatedBy            = CurrentUsername,
                CreatedAt            = DateTime.UtcNow,
                ChangeNotes          = "Versiune inițială",
            });

            AddAudit(AuditAction.DocumentCreate, $"Document publicat '{title}' (SHA-256 {checksum[..16]}…)");

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch
            {
                // Obiectul a ajuns în depozit dar rândul nu s-a salvat: fără
                // compensare ar rămâne acolo invizibil și imposibil de șters.
                await TryDeleteAsync(storageKey);
                throw;
            }

            return Ok(new { id = doc.Id, sha256 = checksum, message = $"Documentul '{title}' a fost publicat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Documents/{id}/versions - versiune nouă
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost("{id:guid}/versions")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "SefDirectie,Administrator")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]
        public async Task<IActionResult> AddVersion(
            Guid id, [FromForm] AddDocumentVersionRequest request, CancellationToken ct)
        {
            var file = request.File;

            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Fișierul este obligatoriu." });

            if (file.Length > _storageOptions.MaxFileSizeBytes)
                return BadRequest(new
                {
                    message = $"Fișierul depășește limita de {_storageOptions.MaxFileSizeMb} MB.",
                });

            var doc = await _context.Documents.FindAsync(new object?[] { id }, ct);
            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });

            var nextVersion = doc.CurrentVersion + 1;
            var safeName    = SanitizeFileName(file.FileName);
            var storageKey  = BuildStorageKey(doc.Id, nextVersion, safeName);

            var (checksum, stored) = await StoreAsync(file, storageKey, ct);
            if (!stored)
                return StatusCode(502, new { message = "Fișierul nu a putut fi scris în depozit." });

            doc.CurrentVersion = nextVersion;

            _context.DocumentVersions.Add(new DocumentVersion
            {
                Id                   = Guid.NewGuid(),
                DocumentId           = id,
                VersionNumber        = nextVersion,
                EncryptedStoragePath = storageKey,
                ChecksumSHA256       = checksum,
                CreatedBy            = CurrentUsername,
                CreatedAt            = DateTime.UtcNow,
                ChangeNotes          = request.ChangeNotes?.Trim() ?? string.Empty,
            });

            AddAudit(AuditAction.DocumentNewVersion,
                $"Versiune noua (v{nextVersion}) la '{doc.Title}' (SHA-256 {checksum[..16]}…)");

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch
            {
                await TryDeleteAsync(storageKey);
                throw;
            }

            return Ok(new { message = $"Versiunea {nextVersion} a fost publicată.", sha256 = checksum });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Documents/{id}/download
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Descărcarea ultimei versiuni.
        ///
        /// Trei schimbări față de varianta anterioară:
        ///
        ///   1. Conținutul se STREAMEAZĂ. Înainte se folosea ReadAllBytesAsync,
        ///      care încarcă tot fișierul în memoria procesului. Cu limita de
        ///      50 MB pe fișier, câteva zeci de cereri simultane la același
        ///      document - un singur utilizator autentificat cu un for în bash -
        ///      erau suficiente ca să doboare API-ul prin epuizarea memoriei.
        ///
        ///   2. Descărcarea se JURNALIZEAZĂ. Într-un registru de acte normative,
        ///      cine a citit ce este exact întrebarea la care trebuie să răspundă
        ///      jurnalul; până acum singura operație neauditată era chiar citirea.
        ///
        ///   3. Numele de fișier propus browserului se curăță. Titlul e text liber
        ///      introdus de un Șef de Direcție și ajungea direct în antetul
        ///      Content-Disposition.
        /// </summary>
        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            var doc = await _context.Documents
                .AsNoTracking()
                .Include(d => d.Versions)
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });

            var ver = doc.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (ver is null || string.IsNullOrWhiteSpace(ver.EncryptedStoragePath))
                return NotFound(new { message = "Documentul nu are nicio versiune stocată." });

            Stream stream;
            try
            {
                stream = await OpenVersionAsync(ver, ct);
            }
            catch (FileNotFoundException)
            {
                _logger.LogError(
                    "Versiune fara obiect in depozit: document {Doc} v{Ver} → {Key}",
                    doc.Id, ver.VersionNumber, ver.EncryptedStoragePath);

                return NotFound(new { message = "Fișierul nu mai există în depozit." });
            }

            AddAudit(AuditAction.FileDownload,
                $"Document descarcat '{doc.Title}' v{ver.VersionNumber}");
            await _context.SaveChangesAsync(ct);

            var ext          = Path.GetExtension(ver.OriginalFileNameOrKey());
            var downloadName = SanitizeFileName(doc.Title + ext);

            return File(stream, "application/octet-stream", downloadName);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cheie de obiect, nu cale de sistem.
        ///
        /// Diferența nu e cosmetică: versiunea anterioară stoca în coloană calea
        /// ABSOLUTĂ de pe server, iar endpointul de descărcare o dădea direct lui
        /// File.ReadAllBytes. Orice bug viitor care ar fi permis scrierea acelei
        /// coloane - un import, o migrare, un endpoint de editare adăugat de
        /// altcineva - s-ar fi transformat instantaneu în citire arbitrară de
        /// fișiere de pe mașină, inclusiv appsettings.json.
        /// </summary>
        private static string BuildStorageKey(Guid documentId, int version, string safeName)
        {
            var ext = Path.GetExtension(safeName);
            if (ext.Length > 16) ext = string.Empty;
            return $"{DocumentsPrefix}/{documentId:N}/v{version}{ext}";
        }

        /// <summary>
        /// Scrie conținutul în depozit și întoarce amprenta SHA-256, calculată
        /// incremental peste octeții primiți efectiv.
        /// </summary>
        private async Task<(string Checksum, bool Stored)> StoreAsync(
            IFormFile file, string storageKey, CancellationToken ct)
        {
            await using var upload = file.OpenReadStream();

            var checksum = await ComputeSha256HexAsync(upload, ct);

            if (!upload.CanSeek)
            {
                _logger.LogError("Stream de upload nerepozitionabil pentru {Key}.", storageKey);
                return (checksum, false);
            }

            upload.Position = 0;

            try
            {
                await _storage.PutAsync(
                    storageKey, upload, file.Length, "application/octet-stream", ct);

                return (checksum, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrierea obiectului {Key} a esuat.", storageKey);
                return (checksum, false);
            }
        }

        /// <summary>
        /// Deschide obiectul unei versiuni.
        ///
        /// Compatibilitate cu rândurile scrise înainte de această schimbare, care
        /// conțin o cale absolută în loc de o cheie. Ele se citesc de pe disc, dar
        /// numai după verificarea că rămân sub directorul de uploads - o cale
        /// absolută dintr-un rând vechi nu devine automat de încredere.
        /// </summary>
        private async Task<Stream> OpenVersionAsync(DocumentVersion version, CancellationToken ct)
        {
            var stored = version.EncryptedStoragePath;

            if (!Path.IsPathRooted(stored))
                return await _storage.OpenReadAsync(stored, ct);

            var legacyRoot = Path.GetFullPath(
                Path.Combine(Directory.GetCurrentDirectory(), "uploads", "documents"));
            var full = Path.GetFullPath(stored);

            if (!full.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Versiune cu cale in afara directorului de uploads: {Path}. Refuzata.", stored);
                throw new FileNotFoundException("Cale de stocare invalidă.", stored);
            }

            if (!System.IO.File.Exists(full))
                throw new FileNotFoundException("Obiectul nu există.", stored);

            return new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81_920, useAsync: true);
        }

        private async Task TryDeleteAsync(string storageKey)
        {
            try { await _storage.DeleteAsync(storageKey, CancellationToken.None); }
            catch (Exception ex) { _logger.LogError(ex, "Retragerea obiectului {Key} a esuat.", storageKey); }
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

        /// <summary>
        /// Curăță un nume de fișier venit de la utilizator.
        ///
        /// Path.GetFileName singur nu ajunge: pe Linux nu tratează '\' ca separator,
        /// deci un nume trimis de pe Windows trece nemodificat. Aici se elimină
        /// ambii separatori, caracterele de control (care ar putea rupe antetul
        /// Content-Disposition) și componentele de tip "..".
        /// </summary>
        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "document";

            var cleaned = new string(name
                .Where(c => !char.IsControl(c) && c != '/' && c != '\\' && c != '"' && c != ':')
                .ToArray())
                .Replace("..", string.Empty)
                .Trim(' ', '.');

            if (string.IsNullOrWhiteSpace(cleaned)) return "document";

            return cleaned.Length <= 200 ? cleaned : cleaned[..200];
        }

        private void AddAudit(AuditAction action, string details,
            AuditResult result = AuditResult.Success)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = CurrentUserId,
                Username  = CurrentUsername,
                Action    = action,
                Details   = details,
                Result    = result,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
        }
    }

    /// <summary>
    /// Numele de afișat pentru o versiune, indiferent dacă rândul conține o cheie
    /// nouă sau o cale absolută dintr-o versiune veche a aplicației.
    /// </summary>
    internal static class DocumentVersionExtensions
    {
        public static string OriginalFileNameOrKey(this DocumentVersion version)
        {
            var stored = version.EncryptedStoragePath ?? string.Empty;
            if (stored.Length == 0) return string.Empty;

            var lastSlash = stored.LastIndexOfAny(['/', '\\']);
            return lastSlash >= 0 ? stored[(lastSlash + 1)..] : stored;
        }
    }
}