using MAI.Api.Services;
using MAI.BusinessLogic.Organization;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Structura organizatorică: Direcții → Secții → Servicii, fiecare cu șef.
    ///
    /// Citirea e deschisă oricărui utilizator autentificat — formularul de
    /// distribuție și filtrele au nevoie de arbore, iar organigrama nu e
    /// informație secretă în interiorul instituției. Orice modificare e
    /// exclusiv a administratorului și ajunge în jurnal (OrgStructureChanged).
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class OrgUnitsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public OrgUnitsController(AppDbContext context) => _context = context;

        private string CallerUsername => HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // ═════════════════════════════════════════════════════════════════════
        // GET api/OrgUnits — tot arborele, ca listă plată
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
        {
            var units = await _context.OrgUnits
                .AsNoTracking()
                .Where(u => includeInactive || u.IsActive)
                .OrderBy(u => u.Type).ThenBy(u => u.Name)
                .Select(u => new OrgUnitDto
                {
                    Id          = u.Id,
                    Name        = u.Name,
                    Code        = u.Code,
                    Type        = u.Type,
                    ParentId    = u.ParentId,
                    HeadUserId  = u.HeadUserId,
                    HeadName    = u.HeadUser != null ? (u.HeadUser.FullName ?? u.HeadUser.Username) : null,
                    IsActive    = u.IsActive,
                    MemberCount = _context.Users.Count(m => m.OrgUnitId == u.Id && m.IsActive),
                })
                .ToListAsync(ct);

            return Ok(units);
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/OrgUnits/{id}/members
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("{id:guid}/members")]
        public async Task<IActionResult> GetMembers(Guid id, CancellationToken ct)
        {
            if (!await _context.OrgUnits.AnyAsync(u => u.Id == id, ct))
                return NotFound(new { message = "Subdiviziunea nu a fost găsită." });

            var members = await _context.Users
                .AsNoTracking()
                .Where(u => u.OrgUnitId == id)
                .OrderBy(u => u.FullName ?? u.Username)
                .Select(u => new
                {
                    id       = u.Id,
                    username = u.Username,
                    fullName = u.FullName ?? u.Username,
                    isActive = u.IsActive,
                    isHead   = _context.OrgUnits.Any(o => o.Id == id && o.HeadUserId == u.Id),
                })
                .ToListAsync(ct);

            return Ok(members);
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/OrgUnits
        // ═════════════════════════════════════════════════════════════════════
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] SaveOrgUnitDto dto, CancellationToken ct)
        {
            var error = await ValidateAsync(null, dto, ct);
            if (error is not null) return BadRequest(new { message = error });

            var unit = new OrgUnit
            {
                Name      = dto.Name.Trim(),
                Code      = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code.Trim(),
                Type      = dto.Type,
                ParentId  = dto.ParentId,
                IsActive  = true,
                CreatedAt = DateTime.UtcNow,
            };

            _context.OrgUnits.Add(unit);
            Audit($"Subdiviziune creata: '{unit.Name}' ({unit.Type})" +
                  (dto.ParentId.HasValue ? $", in subordinea {dto.ParentId}" : ", nivel de varf"));
            await _context.SaveChangesAsync(ct);

            return Ok(new { id = unit.Id, message = $"Subdiviziunea „{unit.Name}” a fost creată." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PUT api/OrgUnits/{id}
        // ═════════════════════════════════════════════════════════════════════
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update(Guid id, [FromBody] SaveOrgUnitDto dto, CancellationToken ct)
        {
            var unit = await _context.OrgUnits.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (unit is null) return NotFound(new { message = "Subdiviziunea nu a fost găsită." });

            var error = await ValidateAsync(id, dto, ct);
            if (error is not null) return BadRequest(new { message = error });

            // Desființarea cere ca subunitățile active să fi fost mutate sau
            // desființate întâi: altfel ar rămâne subunități active sub un
            // părinte invizibil în formulare.
            if (dto.IsActive == false && unit.IsActive &&
                await _context.OrgUnits.AnyAsync(u => u.ParentId == id && u.IsActive, ct))
                return BadRequest(new { message = "Desființați sau mutați întâi subunitățile active." });

            var changes = new List<string>();
            if (unit.Name != dto.Name.Trim()) changes.Add($"denumire '{unit.Name}' -> '{dto.Name.Trim()}'");
            if (unit.Type != dto.Type) changes.Add($"nivel {unit.Type} -> {dto.Type}");
            if (unit.ParentId != dto.ParentId) changes.Add($"parinte {unit.ParentId?.ToString() ?? "-"} -> {dto.ParentId?.ToString() ?? "-"}");
            if (dto.IsActive.HasValue && unit.IsActive != dto.IsActive.Value)
                changes.Add(dto.IsActive.Value ? "reactivata" : "desfiintata");

            unit.Name     = dto.Name.Trim();
            unit.Code     = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code.Trim();
            unit.Type     = dto.Type;
            unit.ParentId = dto.ParentId;
            if (dto.IsActive.HasValue) unit.IsActive = dto.IsActive.Value;

            if (changes.Count > 0)
                Audit($"Subdiviziune modificata '{unit.Name}': {string.Join("; ", changes)}");

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = $"Subdiviziunea „{unit.Name}” a fost actualizată." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PATCH api/OrgUnits/{id}/head
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Numește (sau eliberează) șeful subdiviziunii.
        ///
        /// Șeful este încadrat automat în subdiviziunea pe care o conduce. Dacă
        /// persoana conducea deja altă subdiviziune, acea funcție se eliberează —
        /// un utilizator conduce cel mult o subdiviziune (UX_OrgUnits_HeadUserId).
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpPatch("{id:guid}/head")]
        public async Task<IActionResult> SetHead(Guid id, [FromBody] SetHeadDto? dto, CancellationToken ct)
        {
            var unit = await _context.OrgUnits.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (unit is null) return NotFound(new { message = "Subdiviziunea nu a fost găsită." });
            if (!unit.IsActive) return BadRequest(new { message = "Subdiviziunea este desființată." });

            var userId = dto?.UserId;

            if (userId is null)
            {
                if (unit.HeadUserId is null) return Ok(new { message = "Subdiviziunea nu avea șef." });
                unit.HeadUserId = null;
                Audit($"Functia de sef al subdiviziunii '{unit.Name}' a fost eliberata");
                await _context.SaveChangesAsync(ct);
                return Ok(new { message = $"Funcția de șef al subdiviziunii „{unit.Name}” a fost eliberată." });
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null || !user.IsActive)
                return BadRequest(new { message = "Utilizatorul nu există sau are contul dezactivat." });

            if (unit.HeadUserId == user.Id)
                return Ok(new { message = "Persoana conduce deja această subdiviziune." });

            var notes = new List<string>();

            // Eliberarea funcției vechi și numirea nouă se salvează în doi pași
            // (indexul unic ar vedea altfel același șef la două subdiviziuni),
            // dar într-o singură tranzacție: ori se fac amândouă, ori niciuna.
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            var previous = await _context.OrgUnits.FirstOrDefaultAsync(u => u.HeadUserId == user.Id, ct);
            if (previous is not null)
            {
                previous.HeadUserId = null;
                notes.Add($"a eliberat funcția de șef la „{previous.Name}”");
                await _context.SaveChangesAsync(ct);
            }

            if (user.OrgUnitId != unit.Id)
            {
                user.OrgUnitId = unit.Id;
                notes.Add("a fost încadrat în subdiviziune");
            }

            unit.HeadUserId = user.Id;

            Audit($"Sef numit pentru '{unit.Name}': @{user.Username}" +
                  (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty));
            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(new
            {
                message = $"{user.FullName ?? user.Username} conduce acum „{unit.Name}”" +
                          (notes.Count > 0 ? $" ({string.Join("; ", notes)})." : "."),
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/OrgUnits/{id}
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Ștergerea e posibilă doar pentru o subdiviziune goală și nefolosită:
        /// fără subunități, fără membri, fără documente distribuite. Altfel se
        /// desființează (IsActive = false) și istoricul rămâne corect.
        /// </summary>
        [Authorize(Roles = nameof(UserRole.Administrator))]
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var unit = await _context.OrgUnits.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (unit is null) return NotFound(new { message = "Subdiviziunea nu a fost găsită." });

            if (await _context.OrgUnits.AnyAsync(u => u.ParentId == id, ct))
                return Conflict(new { message = "Subdiviziunea are subunități. Mutați-le sau ștergeți-le întâi." });

            if (await _context.Users.AnyAsync(u => u.OrgUnitId == id, ct))
                return Conflict(new { message = "Subdiviziunea are membri. Mutați-i în altă subdiviziune întâi." });

            var referenced =
                await _context.InternalDocumentRecipients.AnyAsync(r => r.OrgUnitId == id, ct) ||
                await _context.InternalDocuments.AnyAsync(d => d.AuthorOrgUnitId == id, ct) ||
                await _context.InternalDocumentTargets.AnyAsync(t => t.Kind == DistributionTargetKind.Unit && t.TargetId == id, ct);

            if (referenced)
                return Conflict(new
                {
                    message = "Subdiviziunea apare în documente interne distribuite. " +
                              "Desființați-o în loc să o ștergeți, ca rapoartele să rămână corecte.",
                });

            _context.OrgUnits.Remove(unit);
            Audit($"Subdiviziune stearsa: '{unit.Name}'");
            await _context.SaveChangesAsync(ct);

            return Ok(new { message = $"Subdiviziunea „{unit.Name}” a fost ștearsă." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private async Task<string?> ValidateAsync(Guid? id, SaveOrgUnitDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Trim().Length > 200)
                return "Denumirea este obligatorie (maxim 200 de caractere).";

            if (dto.Code is { Length: > 20 })
                return "Prescurtarea are maxim 20 de caractere.";

            if (!Enum.IsDefined(dto.Type))
                return "Nivelul subdiviziunii nu există.";

            var tree = await OrgStructure.LoadTreeAsync(_context, ct);

            if (dto.ParentId.HasValue && tree.Find(dto.ParentId.Value) is { IsActive: false })
                return "Subdiviziunea-părinte este desființată.";

            var placement = tree.ValidatePlacement(id, dto.Type, dto.ParentId);
            if (placement is not null) return placement;

            // Denumire unică între frați: două „Secția analiză” sub aceeași
            // direcție ar fi imposibil de deosebit în formulare.
            var name = dto.Name.Trim().ToLower();
            var duplicate = await _context.OrgUnits.AnyAsync(u =>
                u.Id != id && u.ParentId == dto.ParentId && u.Name.ToLower() == name, ct);

            return duplicate ? "Există deja o subdiviziune cu această denumire la același nivel." : null;
        }

        private void Audit(string details) =>
            _context.AuditLogs.Add(new AuditLog
            {
                Username  = CallerUsername,
                UserId    = Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null,
                Action    = AuditAction.OrgStructureChanged,
                Details   = details,
                Result    = AuditResult.Success,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
    }

    public class OrgUnitDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Code { get; set; }
        public OrgUnitType Type { get; set; }
        public Guid? ParentId { get; set; }
        public Guid? HeadUserId { get; set; }
        public string? HeadName { get; set; }
        public bool IsActive { get; set; }
        public int MemberCount { get; set; }
    }

    public class SaveOrgUnitDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Code { get; set; }
        public OrgUnitType Type { get; set; } = OrgUnitType.Directie;
        public Guid? ParentId { get; set; }

        /// <summary>Doar la actualizare: false = desființare, true = reactivare.</summary>
        public bool? IsActive { get; set; }
    }

    public class SetHeadDto
    {
        public Guid? UserId { get; set; }
    }
}
