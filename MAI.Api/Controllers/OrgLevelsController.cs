using MAI.BusinessLogic.Organization;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Nivelurile structurii organizatorice (Direcție, Secție, Serviciu și cele
    /// adăugate de administrator). Citirea e deschisă tuturor, pentru afișare;
    /// modificarea e doar a administratorului.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class OrgLevelsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public OrgLevelsController(AppDbContext context) => _context = context;

        [HttpGet]
        public async Task<IActionResult> GetAll(CancellationToken ct)
        {
            var levels = await _context.OrgLevels
                .AsNoTracking()
                .OrderBy(l => l.Rank)
                .Select(l => new
                {
                    rank      = l.Rank,
                    name      = l.Name,
                    unitCount = _context.OrgUnits.Count(u => (int)u.Type == l.Rank),
                })
                .ToListAsync(ct);

            return Ok(levels);
        }

        /// <summary>
        /// Adaugă un nivel imediat sub <c>AfterRank</c> (null = deasupra tuturor).
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateOrgLevelDto? dto, CancellationToken ct)
        {
            var name = dto?.Name?.Trim();
            var error = await ValidateNameAsync(name, null, ct);
            if (error is not null) return BadRequest(new { message = error });

            var existing = await _context.OrgLevels.Select(l => l.Rank).ToListAsync(ct);
            var rank = OrgLevelRules.RankAfter(existing, dto!.AfterRank);

            if (rank is null)
                return BadRequest(new
                {
                    message = dto.AfterRank.HasValue && !existing.Contains(dto.AfterRank.Value)
                        ? "Nivelul de referință nu există."
                        : "Nu mai există loc pentru un nivel nou în această poziție. Alegeți altă poziție.",
                });

            _context.OrgLevels.Add(new OrgLevel { Rank = rank.Value, Name = name!, CreatedAt = DateTime.UtcNow });
            Audit($"Nivel de structura adaugat: '{name}' (rang {rank})");
            await _context.SaveChangesAsync(ct);

            return Ok(new { rank, message = $"Nivelul „{name}” a fost adăugat." });
        }

        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPut("{rank:int}")]
        public async Task<IActionResult> Rename(int rank, [FromBody] RenameOrgLevelDto? dto, CancellationToken ct)
        {
            var level = await _context.OrgLevels.FirstOrDefaultAsync(l => l.Rank == rank, ct);
            if (level is null) return NotFound(new { message = "Nivelul nu a fost găsit." });

            var name = dto?.Name?.Trim();
            var error = await ValidateNameAsync(name, rank, ct);
            if (error is not null) return BadRequest(new { message = error });

            Audit($"Nivel de structura redenumit: '{level.Name}' -> '{name}'");
            level.Name = name!;
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Nivelul a fost redenumit în „{name}”." });
        }

        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpDelete("{rank:int}")]
        public async Task<IActionResult> Delete(int rank, CancellationToken ct)
        {
            var level = await _context.OrgLevels.FirstOrDefaultAsync(l => l.Rank == rank, ct);
            if (level is null) return NotFound(new { message = "Nivelul nu a fost găsit." });

            if (await _context.OrgUnits.AnyAsync(u => (int)u.Type == rank, ct))
                return Conflict(new { message = $"Există subdiviziuni pe nivelul „{level.Name}”. Mutați-le pe alt nivel întâi." });

            if (await _context.OrgLevels.CountAsync(ct) <= 1)
                return Conflict(new { message = "Structura trebuie să aibă cel puțin un nivel." });

            _context.OrgLevels.Remove(level);
            Audit($"Nivel de structura sters: '{level.Name}'");
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Nivelul „{level.Name}” a fost șters." });
        }

        private async Task<string?> ValidateNameAsync(string? name, int? exceptRank, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60)
                return "Denumirea nivelului este obligatorie (maxim 60 de caractere).";

            var lower = name.ToLower();
            return await _context.OrgLevels.AnyAsync(l => l.Rank != exceptRank && l.Name.ToLower() == lower, ct)
                ? "Există deja un nivel cu această denumire."
                : null;
        }

        private void Audit(string details) =>
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null,
                Username  = User.Identity?.Name ?? "sistem",
                Action    = AuditAction.OrgStructureChanged,
                Details   = details,
                Result    = AuditResult.Success,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Timestamp = DateTime.UtcNow,
            });
    }

    public class CreateOrgLevelDto
    {
        public string? Name { get; set; }

        /// <summary>Nivelul imediat superior celui nou. Null = nivel de vârf.</summary>
        public int? AfterRank { get; set; }
    }

    public class RenameOrgLevelDto
    {
        public string? Name { get; set; }
    }
}
