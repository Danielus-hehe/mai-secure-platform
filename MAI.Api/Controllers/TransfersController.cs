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

        // GET api/Transfers
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var userId = CurrentUserId;

            var raw = await _context.FileTransfers
                .Include(t => t.Sender)
                .Include(t => t.Recipient)
                .Where(t => t.SenderId == userId || t.RecipientId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .Take(100)
                .ToListAsync();

            var result = raw.Select(t => new
            {
                id                  = t.Id,
                fileName            = t.FileName,
                fileSize            = t.FileSize,
                sha256              = t.ChecksumSHA256,
                senderName          = t.Sender?.FullName  ?? t.Sender?.Username  ?? "—",
                senderDepartment    = t.Sender?.Department ?? string.Empty,
                recipientName       = t.Recipient?.FullName ?? t.Recipient?.Username ?? "—",
                recipientDepartment = t.Recipient?.Department ?? string.Empty,
                status              = t.Status.ToString(),
                createdAt           = t.CreatedAt,
                downloadedAt        = t.DownloadedAt,
                isMine              = t.SenderId == userId,
            });

            return Ok(result);
        }

        // POST api/Transfers
        // recipientId vine ca STRING (nu Guid) — Guid în [FromForm] poate crăpa Swashbuckle
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Upload([FromForm] UploadTransferRequest request)
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

            var recipient = await _context.Users.FindAsync(recipientGuid);
            if (recipient is null)
                return BadRequest(new { message = "Destinatarul nu a fost găsit." });

            var senderId = CurrentUserId;
            if (senderId == recipientGuid)
                return BadRequest(new { message = "Nu poți trimite un fișier ție însuți." });

            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "transfers");
            Directory.CreateDirectory(uploadsPath);
            var storageName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
            var fullPath    = Path.Combine(uploadsPath, storageName);

            await using (var stream = new FileStream(fullPath, FileMode.Create))
                await file.CopyToAsync(stream);

            var transfer = new FileTransfer
            {
                Id                   = Guid.NewGuid(),
                SenderId             = senderId,
                RecipientId          = recipientGuid,
                FileName             = file.FileName,
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
                Details   = $"SUCCES: Fișier trimis '{file.FileName}' → {recipient.FullName ?? recipient.Username}",
                IpAddress = CallerIp,
            });
            await _context.SaveChangesAsync();

            return Ok(new { id = transfer.Id, message = $"Fișierul '{file.FileName}' a fost trimis." });
        }

        // PATCH api/Transfers/{id}/confirm
        [HttpPatch("{id:guid}/confirm")]
        public async Task<IActionResult> Confirm(Guid id)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers
                .Include(t => t.Sender)
                .FirstOrDefaultAsync(t => t.Id == id);

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
            });
            await _context.SaveChangesAsync();

            return Ok(new { message = "Transfer confirmat." });
        }

        // GET api/Transfers/{id}/download
        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> Download(Guid id)
        {
            var userId   = CurrentUserId;
            var transfer = await _context.FileTransfers.FindAsync(id);

            if (transfer is null) return NotFound(new { message = "Transferul nu a fost găsit." });
            if (transfer.SenderId != userId && transfer.RecipientId != userId) return Forbid();
            if (!System.IO.File.Exists(transfer.EncryptedStoragePath))
                return NotFound(new { message = "Fișierul nu mai există pe server." });

            var bytes = await System.IO.File.ReadAllBytesAsync(transfer.EncryptedStoragePath);
            return File(bytes, "application/octet-stream", transfer.FileName);
        }
    }
}