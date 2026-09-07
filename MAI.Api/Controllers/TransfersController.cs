using MAI.Api.Models;
using MAI.BusinessLogic.Dtos;
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
    public class TransfersController : ControllerBase
    {
        private readonly AppDbContext _context;
        public TransfersController(AppDbContext context) => _context = context;

        private Guid CurrentUserId =>
            Guid.Parse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        private string CurrentUsername =>
            HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // ─────────────────────────────────────────────────────────────────────
        // GET api/Transfers?search=&status=&direction=&sortBy=&sortDir=&page=&pageSize=
        // ─────────────────────────────────────────────────────────────────────
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

            // Direcție: trimise de mine / primite de mine
            if (string.Equals(direction, "sent", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.SenderId == userId);
            else if (string.Equals(direction, "received", StringComparison.OrdinalIgnoreCase))
                query = query.Where(t => t.RecipientId == userId);

            // Status
            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<TransferStatus>(status, ignoreCase: true, out var statusEnum))
            {
                query = query.Where(t => t.Status == statusEnum);
            }

            // Căutare server-side: nume fișier, expeditor, destinatar.
            // ILike se traduce în ILIKE PostgreSQL, deci filtrarea rămâne în DB —
            // înainte se aduceau 100 de rânduri și se filtra în browser.
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

            var items = raw.Select(t => new TransferDto
            {
                Id                  = t.Id,
                FileName            = t.FileName,
                FileSize            = t.FileSize,
                Sha256              = t.ChecksumSHA256,
                SenderName          = t.Sender?.FullName    ?? t.Sender?.Username    ?? "—",
                SenderDepartment    = t.Sender?.Department   ?? string.Empty,
                RecipientName       = t.Recipient?.FullName  ?? t.Recipient?.Username ?? "—",
                RecipientDepartment = t.Recipient?.Department ?? string.Empty,
                Status              = t.Status.ToString(),
                CreatedAt           = t.CreatedAt,
                DownloadedAt        = t.DownloadedAt,
                IsMine              = t.SenderId == userId,
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
                "filename"  => asc ? query.OrderBy(t => t.FileName)  : query.OrderByDescending(t => t.FileName),
                "filesize"  => asc ? query.OrderBy(t => t.FileSize)  : query.OrderByDescending(t => t.FileSize),
                "status"    => asc ? query.OrderBy(t => t.Status)    : query.OrderByDescending(t => t.Status),
                _           => asc ? query.OrderBy(t => t.CreatedAt) : query.OrderByDescending(t => t.CreatedAt),
            };
        }

        // POST api/Transfers
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Upload([FromForm] UploadTransferRequest request, CancellationToken ct)
        {
            var file        = request.File;
            var recipientId = request.RecipientId;
            var sha256      = request.Sha256;

            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Fișierul este obligatoriu." });

            if (!Guid.TryParse(recipientId, out var recipientGuid))
                return BadRequest(new { message = "recipientId invalid." });

            if (file.Length > 50 * 1024 * 1024)
                return BadRequest(new { message = "Fișierul depășește limita de 50 MB." });

            var recipient = await _context.Users.FindAsync(new object?[] { recipientGuid }, ct);
            if (recipient is null)
                return BadRequest(new { message = "Destinatarul nu a fost găsit." });

            var senderId = CurrentUserId;
            if (senderId == recipientGuid)
                return BadRequest(new { message = "Nu poți trimite un fișier ție însuți." });

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "transfers");
            Directory.CreateDirectory(uploadsPath);

            // Path.GetFileName elimină componentele de cale din numele trimis de client,
            // altfel un fileName de forma "../../appsettings.json" ar scrie în afara folderului.
            var safeName    = Path.GetFileName(file.FileName);
            var storageName = $"{Guid.NewGuid()}_{safeName}";
            var fullPath    = Path.Combine(uploadsPath, storageName);

            await using (var stream = new FileStream(fullPath, FileMode.Create))
                await file.CopyToAsync(stream, ct);

            var transfer = new FileTransfer
            {
                Id                   = Guid.NewGuid(),
                SenderId             = senderId,
                RecipientId          = recipientGuid,
                FileName             = safeName,
                EncryptedStoragePath = fullPath,
                FileSize             = file.Length,
                ChecksumSHA256       = sha256 ?? string.Empty,
                Status               = TransferStatus.Pending,
                CreatedAt            = DateTime.UtcNow,
            };

            _context.FileTransfers.Add(transfer);
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = senderId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileUpload,
                Details   = $"SUCCES: Fișier trimis '{safeName}' → {recipient.FullName ?? recipient.Username}",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);

            return Ok(new { id = transfer.Id, message = $"Fișierul '{safeName}' a fost trimis." });
        }

        // PATCH api/Transfers/{id}/confirm
        [HttpPatch("{id:guid}/confirm")]
        public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers
                .Include(t => t.Sender)
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.RecipientId != userId) return Forbid();

            transfer.Status       = TransferStatus.Downloaded;
            transfer.DownloadedAt = DateTime.UtcNow;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDownload,
                Details   = $"SUCCES: Transfer confirmat '{transfer.FileName}'",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = "Transfer confirmat." });
        }

        // GET api/Transfers/{id}/download
        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers.FindAsync(new object?[] { id }, ct);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.SenderId != userId && transfer.RecipientId != userId) return Forbid();
            if (!System.IO.File.Exists(transfer.EncryptedStoragePath))
                return NotFound(new { message = "Fișierul nu mai există pe server." });

            // Descărcarea de către destinatar marchează automat transferul ca preluat —
            // era un punct deschis din lista de priorități.
            if (transfer.RecipientId == userId && transfer.Status == TransferStatus.Pending)
            {
                transfer.Status       = TransferStatus.Downloaded;
                transfer.DownloadedAt = DateTime.UtcNow;
            }

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = CurrentUsername,
                Action    = AuditAction.FileDownload,
                Details   = $"SUCCES: Fișier descărcat '{transfer.FileName}'",
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);

            var bytes = await System.IO.File.ReadAllBytesAsync(transfer.EncryptedStoragePath, ct);
            return File(bytes, "application/octet-stream", transfer.FileName);
        }
    }

    public class TransferDto
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string SenderDepartment { get; set; } = string.Empty;
        public string RecipientName { get; set; } = string.Empty;
        public string RecipientDepartment { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? DownloadedAt { get; set; }
        public bool IsMine { get; set; }
    }
}