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
    public class AuditLogsController : ControllerBase
    {
        private readonly AppDbContext _context;
        public AuditLogsController(AppDbContext context) => _context = context;

        // GET api/AuditLogs?username=&action=&result=
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? username,
            [FromQuery] string? action,
            [FromQuery] string? result)
        {
            var query = _context.AuditLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(username))
                query = query.Where(a => a.Username == username);

            if (!string.IsNullOrWhiteSpace(action))
            {
                var enums = MapFrontendAction(action);
                if (enums is { Length: > 0 })
                    query = query.Where(a => enums.Contains(a.Action));
            }

            if (result == "SUCCES")
                query = query.Where(a => !a.Details.StartsWith("ESEC"));
            else if (result == "ESEC")
                query = query.Where(a => a.Details.StartsWith("ESEC"));

            // FIX: ToListAsync() primul, apoi proiecția cu range operator în memorie
            var raw = await query
                .OrderByDescending(a => a.Timestamp)
                .Take(500)
                .ToListAsync();

            var logs = raw.Select(a => new
            {
                id        = a.Id,
                timestamp = a.Timestamp,
                userId    = a.UserId,
                userName  = a.Username,
                action    = MapBackendAction(a.Action),
                // Range operator [x..] e valid în memorie, nu în expression tree EF
                target    = a.Details.Contains(':')
                                ? a.Details[(a.Details.IndexOf(':') + 1)..].Trim()
                                : a.Details,
                ipAddress = a.IpAddress,
                result    = a.Details.StartsWith("ESEC") ? "ESEC" : "SUCCES",
            });

            return Ok(logs);
        }

        // GET api/AuditLogs/usernames — pentru dropdown-ul de filtrare
        [HttpGet("usernames")]
        public async Task<IActionResult> GetUsernames()
        {
            var names = await _context.AuditLogs
                .Select(a => a.Username)
                .Distinct()
                .OrderBy(n => n)
                .ToListAsync();
            return Ok(names);
        }

        // ── Mapări enum ──────────────────────────────────────────────────────

        private static string MapBackendAction(AuditAction a) => a switch
        {
            AuditAction.Login              => "LOGIN",
            AuditAction.Logout             => "LOGOUT",
            AuditAction.FileUpload         => "UPLOAD",
            AuditAction.FileDownload       => "DOWNLOAD",
            AuditAction.DocumentCreate     => "MODIFICARE_DOC",
            AuditAction.DocumentNewVersion => "MODIFICARE_DOC",
            AuditAction.UserCreated        => "ADMIN",
            AuditAction.UserUpdated        => "ADMIN",
            _                              => "ADMIN",
        };

        private static AuditAction[]? MapFrontendAction(string s) => s switch
        {
            "LOGIN"          => [AuditAction.Login],
            "LOGOUT"         => [AuditAction.Logout],
            "UPLOAD"         => [AuditAction.FileUpload],
            "DOWNLOAD"       => [AuditAction.FileDownload],
            "MODIFICARE_DOC" => [AuditAction.DocumentCreate, AuditAction.DocumentNewVersion],
            "ADMIN"          => [AuditAction.UserCreated, AuditAction.UserUpdated],
            _                => null,
        };
    }
}