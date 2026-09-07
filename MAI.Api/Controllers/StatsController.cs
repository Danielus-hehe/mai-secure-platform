using MAI.DataAccessLayer;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class StatsController : ControllerBase
    {
        private readonly AppDbContext _context;
        public StatsController(AppDbContext context) => _context = context;

        // GET api/Stats
        [HttpGet]
        public async Task<IActionResult> GetStats()
        {
            var yesterday = DateTime.UtcNow.AddHours(-24);

            var activeUsers          = await _context.Users.CountAsync(u => u.IsActive);
            var totalTransfers       = await _context.FileTransfers.CountAsync();
            var pendingTransfers     = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Pending);
            var totalDocuments       = await _context.Documents.CountAsync();
            var failedLoginsLast24h  = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.Login &&
                a.Details.StartsWith("ESEC") &&
                a.Timestamp >= yesterday);

            var recentTransfers = await _context.FileTransfers
                .Include(t => t.Sender)
                .Include(t => t.Recipient)
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .Select(t => new
                {
                    id            = t.Id,
                    fileName      = t.FileName,
                    fileSize      = t.FileSize,
                    senderName    = t.Sender != null ? (t.Sender.FullName ?? t.Sender.Username) : "—",
                    recipientName = t.Recipient != null ? (t.Recipient.FullName ?? t.Recipient.Username) : "—",
                    status        = t.Status.ToString(),   // "Pending" | "Downloaded" | "Expired"
                    createdAt     = t.CreatedAt,
                })
                .ToListAsync();

            return Ok(new
            {
                activeUsers,
                totalTransfers,
                pendingTransfers,
                totalDocuments,
                failedLoginsLast24h,
                recentTransfers,
            });
        }
    }
}