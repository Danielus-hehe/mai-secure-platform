using MAI.Api.Models;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly AppDbContext _context;
        public DocumentsController(AppDbContext context) => _context = context;

        private Guid CurrentUserId =>
            Guid.Parse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        private string CurrentUsername =>
            HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // GET api/Documents?search=
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? search)
        {
            var query = _context.Documents
                .Include(d => d.Versions)
                .Include(d => d.CreatedBy)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToLower();
                query = query.Where(d =>
                    d.Title.ToLower().Contains(s)          ||
                    d.DocumentNumber.ToLower().Contains(s) ||
                    d.Keywords.ToLower().Contains(s));
            }

            var docs = await query
                .OrderByDescending(d => d.CreatedAt)
                .Take(200)
                .ToListAsync();

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
                publishedBy    = d.CreatedBy?.FullName ?? d.CreatedBy?.Username ?? "—",
                publishedAt    = d.CreatedAt,
                versions       = d.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new
                    {
                        version     = v.VersionNumber,
                        fileName    = Path.GetFileName(v.EncryptedStoragePath),
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

        // POST api/Documents — publică document nou
        // [Consumes] necesar pentru Swagger să nu arunce 500 la swagger.json
        [HttpPost]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "SefDirectie,Administrator")]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Create([FromForm] CreateDocumentRequest request)
        {
            var title    = request.Title;
            var number   = request.Number;
            var category = request.Category;
            var keywords = request.Keywords;
            var file     = request.File;

            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { message = "Titlul este obligatoriu." });

            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Fișierul este obligatoriu." });

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "documents");
            Directory.CreateDirectory(uploadsPath);
            var storageName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
            var fullPath    = Path.Combine(uploadsPath, storageName);

            await using (var stream = new FileStream(fullPath, FileMode.Create))
                await file.CopyToAsync(stream);

            var doc = new Document
            {
                Id             = Guid.NewGuid(),
                Title          = title.Trim(),
                DocumentNumber = number?.Trim() ?? "—",
                Category       = category,
                Keywords       = keywords?.Trim() ?? string.Empty,
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
                EncryptedStoragePath = fullPath,
                ChecksumSHA256       = string.Empty,
                CreatedBy            = CurrentUsername,
                CreatedAt            = DateTime.UtcNow,
                ChangeNotes          = "Versiune inițială",
            });
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = CurrentUserId,
                Username  = CurrentUsername,
                Action    = AuditAction.DocumentCreate,
                Details   = $"SUCCES: Document publicat '{title}'",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();

            return Ok(new { id = doc.Id, message = $"Documentul '{title}' a fost publicat." });
        }

        // POST api/Documents/{id}/versions — versiune nouă
        [HttpPost("{id:guid}/versions")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "SefDirectie,Administrator")]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> AddVersion(
            Guid id,
            [FromForm] AddDocumentVersionRequest request)
        {
            var file        = request.File;
            var changeNotes = request.ChangeNotes;

            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Fișierul este obligatoriu." });

            var doc = await _context.Documents.FindAsync(id);
            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "documents");
            Directory.CreateDirectory(uploadsPath);
            var storageName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
            var fullPath    = Path.Combine(uploadsPath, storageName);

            await using (var stream = new FileStream(fullPath, FileMode.Create))
                await file.CopyToAsync(stream);

            doc.CurrentVersion++;
            _context.DocumentVersions.Add(new DocumentVersion
            {
                Id                   = Guid.NewGuid(),
                DocumentId           = id,
                VersionNumber        = doc.CurrentVersion,
                EncryptedStoragePath = fullPath,
                ChecksumSHA256       = string.Empty,
                CreatedBy            = CurrentUsername,
                CreatedAt            = DateTime.UtcNow,
                ChangeNotes          = changeNotes?.Trim() ?? string.Empty,
            });
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = CurrentUserId,
                Username  = CurrentUsername,
                Action    = AuditAction.DocumentNewVersion,
                Details   = $"SUCCES: Versiune nouă (v{doc.CurrentVersion}) la '{doc.Title}'",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Versiunea {doc.CurrentVersion} a fost publicată." });
        }

        // GET api/Documents/{id}/download
        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> Download(Guid id)
        {
            var doc = await _context.Documents
                .Include(d => d.Versions)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (doc is null) return NotFound();

            var ver = doc.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (ver is null || !System.IO.File.Exists(ver.EncryptedStoragePath))
                return NotFound(new { message = "Fișierul nu mai există pe server." });

            var bytes = await System.IO.File.ReadAllBytesAsync(ver.EncryptedStoragePath);
            var ext   = Path.GetExtension(ver.EncryptedStoragePath);
            return File(bytes, "application/octet-stream", doc.Title + ext);
        }
    }
}