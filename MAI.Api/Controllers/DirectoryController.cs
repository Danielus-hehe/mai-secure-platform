using MAI.Api.Security;
using MAI.Api.Services;
using MAI.BusinessLogic.Ldap;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Administrarea integrării cu Active Directory: starea configurării, testul
    /// de conexiune, importul structurii din unitățile organizatorice și
    /// legarea conturilor locale de conturile de domeniu.
    ///
    /// Totul este doar-citire față de AD. Aplicația nu creează, nu modifică și
    /// nu șterge nimic în domeniu; într-o instituție, dreptul de scriere în AD
    /// nu se dă unei aplicații web, oricât de convenabil ar fi.
    /// </summary>
    [Authorize(Roles = nameof(UserRole.Administrator))]
    [ApiController]
    [Route("api/[controller]")]
    public class DirectoryController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IDirectoryService _directory;
        private readonly IDirectoryAccountProvisioner _provisioner;
        private readonly LdapOptions _options;
        private readonly ILogger<DirectoryController> _logger;

        public DirectoryController(
            AppDbContext context,
            IDirectoryService directory,
            IDirectoryAccountProvisioner provisioner,
            LdapOptions options,
            ILogger<DirectoryController> logger)
        {
            _context     = context;
            _directory   = directory;
            _provisioner = provisioner;
            _options     = options;
            _logger      = logger;
        }

        private string CallerUsername => HttpContext.User.Identity?.Name ?? "sistem";
        private string CallerIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        private Guid CallerId =>
            Guid.TryParse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : Guid.Empty;

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Directory/login-info - public, pentru pagina de autentificare
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Spune paginii de login dacă se acceptă conturi de domeniu și sub ce
        /// nume se scriu. Nu divulgă nimic exploatabil: numele domeniului e
        /// afișat oricum pe fiecare stație din instituție, iar fără indicația
        /// asta utilizatorii încearcă formate greșite până se blochează singuri.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("login-info")]
        public IActionResult LoginInfo() => Ok(new
        {
            enabled = _directory.Enabled,
            domain  = _directory.Enabled ? _options.NetbiosDomain : string.Empty,
            realm   = _directory.Enabled ? _options.Realm : string.Empty,
        });

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Directory/status
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("status")]
        public async Task<IActionResult> Status(CancellationToken ct)
        {
            var linked = await _context.Users
                .CountAsync(u => u.AuthProvider == AuthProvider.Ldap, ct);

            var importedUnits = await _context.OrgUnits
                .CountAsync(u => u.DirectoryDn != null, ct);

            return Ok(new
            {
                enabled       = _directory.Enabled,
                configuration = _options.Describe(),
                roleMappings  = LdapRoleMapper.Parse(_options.GroupRoleMappings)
                    .Select(m => new { group = m.Group, role = m.Role.ToString() }),
                defaultRole   = _options.DefaultRoleValue.ToString(),
                linkedAccounts = linked,
                importedOrgUnits = importedUnits,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Directory/test-connection
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Deschide o conexiune reală la DC, validează certificatul și încearcă
        /// bind-ul contului de serviciu. Întoarce și amprenta certificatului, ca
        /// administratorul să o poată fixa în LDAP_CERT_THUMBPRINT fără să o
        /// caute pe server.
        /// </summary>
        [HttpPost("test-connection")]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        public async Task<IActionResult> TestConnection(CancellationToken ct)
        {
            if (!_directory.Enabled)
                return BadRequest(new { message = "Integrarea cu Active Directory este dezactivată (LDAP_ENABLED=false)." });

            var probe = await _directory.TestConnectionAsync(ct);

            _logger.LogInformation(
                "Test conexiune AD cerut de {Admin}: accesibil={Reachable}, cont de serviciu={Bound}",
                CallerUsername, probe.Reachable, probe.ServiceAccountBound);

            return Ok(probe);
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Directory/org-units/preview
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Calculează ce s-ar schimba în structura noastră dacă s-ar importa
        /// unitățile organizatorice din AD. Nu scrie nimic.
        /// </summary>
        [HttpGet("org-units/preview")]
        public async Task<IActionResult> PreviewOrgUnits(CancellationToken ct)
        {
            if (!_directory.Enabled)
                return BadRequest(new { message = "Integrarea cu Active Directory este dezactivată." });

            IReadOnlyList<DirectoryOrgUnit> units;
            try
            {
                units = await _directory.GetOrganizationalUnitsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AD: citirea unitatilor organizationale a esuat");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unitățile organizatorice nu au putut fi citite din AD: " + ex.Message,
                });
            }

            var plan = await BuildPlanAsync(units, ct);

            return Ok(new
            {
                searchBase = _options.EffectiveOrgUnitSearchBase,
                found      = units.Count,
                toCreate   = plan.ToCreate,
                toUpdate   = plan.ToUpdate,
                unchanged  = plan.Unchanged,
                warnings   = plan.Warnings,
                items      = plan.Items.Select(i => new
                {
                    dn       = i.DistinguishedName,
                    name     = i.Name,
                    parentDn = i.ParentDn,
                    depth    = i.Depth,
                    rank     = i.Rank,
                    action   = i.Action.ToString(),
                    note     = i.Note,
                }),
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Directory/org-units/import
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Aplică importul, doar pentru unitățile confirmate de administrator.
        ///
        /// Confirmarea e obligatorie și explicită: structura organizatorică
        /// decide cine primește documentele interne, deci nu se rescrie automat
        /// pe baza a ce se întâmplă să conțină AD-ul într-o zi anume.
        /// </summary>
        [HttpPost("org-units/import")]
        public async Task<IActionResult> ImportOrgUnits(
            [FromBody] OrgUnitImportRequest? request, CancellationToken ct)
        {
            if (!_directory.Enabled)
                return BadRequest(new { message = "Integrarea cu Active Directory este dezactivată." });

            if (request?.Confirm != true)
                return BadRequest(new { message = "Importul trebuie confirmat explicit (confirm = true)." });

            var units = await _directory.GetOrganizationalUnitsAsync(ct);
            var plan  = await BuildPlanAsync(units, ct);

            var selected = request.DistinguishedNames is { Count: > 0 }
                ? new HashSet<string>(request.DistinguishedNames, StringComparer.OrdinalIgnoreCase)
                : null;

            var items = plan.Items
                .Where(i => i.Action != OrgUnitImportAction.Unchanged)
                .Where(i => selected is null || selected.Contains(i.DistinguishedName))
                .OrderBy(i => i.Depth)
                .ToList();

            if (items.Count == 0)
                return Ok(new { message = "Nu este nimic de importat.", created = 0, updated = 0 });

            // Nivelurile lipsă se creează înaintea subdiviziunilor: OrgUnit.Type
            // trebuie să corespundă unui rang existent, altfel pagina „Structura
            // organizatorică” afișează subdiviziuni pe un nivel fără denumire.
            var existingRanks = await _context.OrgLevels.Select(l => l.Rank).ToListAsync(ct);
            foreach (var rank in items.Select(i => i.Rank).Distinct().Where(r => !existingRanks.Contains(r)))
            {
                _context.OrgLevels.Add(new OrgLevel
                {
                    Rank      = rank,
                    Name      = $"Nivel {rank}",
                    CreatedAt = DateTime.UtcNow,
                });
                existingRanks.Add(rank);
            }

            // DN → Id, ca părintele să poată fi legat imediat după ce e creat.
            var byDn = await _context.OrgUnits
                .Where(u => u.DirectoryDn != null)
                .ToDictionaryAsync(u => u.DirectoryDn!, u => u.Id, StringComparer.OrdinalIgnoreCase, ct);

            var created = 0;
            var updated = 0;

            foreach (var item in items)
            {
                Guid? parentId = item.ParentDn is not null && byDn.TryGetValue(item.ParentDn, out var pid)
                    ? pid
                    : null;

                if (item.Action == OrgUnitImportAction.Create)
                {
                    var unit = new OrgUnit
                    {
                        Name        = item.Name,
                        Type        = (OrgUnitType)item.Rank,
                        ParentId    = parentId,
                        DirectoryDn = item.DistinguishedName,
                        IsActive    = true,
                        CreatedAt   = DateTime.UtcNow,
                    };

                    _context.OrgUnits.Add(unit);
                    byDn[item.DistinguishedName] = unit.Id;
                    created++;
                    continue;
                }

                var existing = await _context.OrgUnits
                    .FirstOrDefaultAsync(u => u.Id == item.ExistingId, ct);

                if (existing is null) continue;

                existing.Name        = item.Name;
                existing.Type        = (OrgUnitType)item.Rank;
                existing.DirectoryDn = item.DistinguishedName;

                // Părintele se schimbă doar dacă avem unul din import. O
                // subdiviziune mutată manual de administrator nu se readuce sub
                // părintele din AD fără să fie nevoie.
                if (parentId.HasValue && existing.ParentId != parentId && existing.Id != parentId)
                    existing.ParentId = parentId;

                byDn[item.DistinguishedName] = existing.Id;
                updated++;
            }

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = CallerId == Guid.Empty ? null : CallerId,
                Username  = CallerUsername,
                Action    = AuditAction.DirectoryStructureImported,
                Details   = $"Import structura din AD ({_options.EffectiveOrgUnitSearchBase}): " +
                            $"{created} subdiviziuni create, {updated} actualizate",
                Result    = AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Import AD confirmat de {Admin}: {Created} create, {Updated} actualizate",
                CallerUsername, created, updated);

            return Ok(new
            {
                message = $"{created} subdiviziuni create, {updated} actualizate. " +
                          "Încadrarea utilizatorilor nu a fost modificată.",
                created,
                updated,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Directory/users/{id}/link
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Leagă un cont local existent de un cont din domeniu. De acum, parola
        /// lui se verifică în AD, iar hash-ul local se șterge.
        ///
        /// Atenție la cheile E2EE: ele sunt încuiate cu parola veche, locală.
        /// Dacă parola de domeniu e alta, utilizatorul trebuie să le
        /// reîmpacheteze o dată, cu parola veche și cea de domeniu (ecranul de
        /// deblocare oferă exact acest pas). Cheile publice rămân neschimbate,
        /// deci fișierele primite anterior nu se pierd.
        /// </summary>
        [HttpPost("users/{id:guid}/link")]
        [EnableRateLimiting(RateLimitPolicies.PasswordWrite)]
        public async Task<IActionResult> LinkAccount(
            Guid id, [FromBody] LinkAccountRequest? request, CancellationToken ct)
        {
            if (!_directory.Enabled)
                return BadRequest(new { message = "Integrarea cu Active Directory este dezactivată." });

            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            if (user.IsDirectoryAccount)
                return BadRequest(new { message = $"Contul @{user.Username} este deja legat de domeniu." });

            var account = DirectoryUsername.Normalize(
                string.IsNullOrWhiteSpace(request?.SamAccountName) ? user.Username : request!.SamAccountName);

            DirectoryUser? found;
            try
            {
                found = await _directory.FindUserAsync(account, ct);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Căutarea în AD a eșuat: " + ex.Message,
                });
            }

            if (found is null)
                return NotFound(new { message = $"Contul „{account}” nu a fost găsit în Active Directory." });

            var taken = await _context.Users
                .AnyAsync(u => u.Id != user.Id && u.DirectoryObjectId == found.ObjectId, ct);

            if (taken)
                return Conflict(new { message = "Acest cont de domeniu este deja legat de alt utilizator." });

            var hadKeys = user.HasKeys;

            user.AuthProvider           = AuthProvider.Ldap;
            user.DirectoryObjectId      = found.ObjectId;
            user.DirectoryDn            = found.DistinguishedName;
            user.DirectoryPasswordSetAt = found.PasswordSetAt;
            user.DirectorySyncedAt      = DateTime.UtcNow;
            user.EmailConfirmed         = true;
            user.MustChangePassword     = false;

            // Hash-ul local dispare: cât timp rămâne, ar exista o a doua parolă
            // validă pentru același om, necunoscută de administratorul de
            // domeniu și nesupusă politicii lui de expirare.
            user.PasswordHash = string.Empty;

            // Reperul pentru detectarea schimbării parolei în AD. Cheile de până
            // acum au fost încuiate la generare, deci acela e momentul de la care
            // se compară pwdLastSet.
            if (hadKeys && user.KeysWrappedAt is null)
                user.KeysWrappedAt = user.KeysCreatedAt;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.DirectoryAccountProvisioned,
                Details   = $"Cont @{user.Username} legat de contul de domeniu {account} " +
                            $"({found.DistinguishedName}); parola locala stearsa",
                Result    = AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message = $"Contul @{user.Username} se autentifică de acum în domeniu." +
                          (hadKeys
                              ? " Are chei E2EE încuiate cu parola locală veche: la prima autentificare " +
                                "i se va cere o dată parola veche, pentru reîmpachetare."
                              : string.Empty),
                distinguishedName = found.DistinguishedName,
                keysNeedRewrap    = hadKeys,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Directory/users/{id}/unlink
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Desface legătura cu domeniul. Contul rămâne fără nicio parolă
        /// utilizabilă până când administratorul îi stabilește una: exact ce
        /// trebuie, ca desfacerea legăturii să nu deschidă contul cu o parolă
        /// veche, rămasă din trecut.
        /// </summary>
        [HttpPost("users/{id:guid}/unlink")]
        public async Task<IActionResult> UnlinkAccount(Guid id, CancellationToken ct)
        {
            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            if (!user.IsDirectoryAccount)
                return BadRequest(new { message = $"Contul @{user.Username} nu este legat de domeniu." });

            user.AuthProvider           = AuthProvider.Local;
            user.DirectoryObjectId      = null;
            user.DirectoryDn            = null;
            user.DirectoryPasswordSetAt = null;
            user.DirectorySyncedAt      = null;
            user.PasswordHash           = string.Empty;
            user.MustChangePassword     = true;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = CallerUsername,
                Action    = AuditAction.UserUpdated,
                Details   = $"Cont @{user.Username} deconectat de la domeniu; necesita parola locala noua",
                Result    = AuditResult.Warning,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });

            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message = $"Contul @{user.Username} nu mai folosește domeniul. " +
                          "Stabiliți-i o parolă din pagina de utilizatori; până atunci nu se poate autentifica.",
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/Directory/users/{id}/sync
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Reia atributele din AD fără să aștepte următoarea autentificare.
        /// Util după o mutare între subdiviziuni sau o schimbare de grupuri.
        /// </summary>
        [HttpPost("users/{id:guid}/sync")]
        public async Task<IActionResult> SyncAccount(Guid id, CancellationToken ct)
        {
            if (!_directory.Enabled)
                return BadRequest(new { message = "Integrarea cu Active Directory este dezactivată." });

            var user = await _context.Users.FindAsync(new object?[] { id }, ct);
            if (user is null) return NotFound(new { message = "Utilizatorul nu a fost găsit." });

            if (!user.IsDirectoryAccount)
                return BadRequest(new { message = $"Contul @{user.Username} nu este de domeniu." });

            var found = await _directory.FindUserAsync(user.Username, ct);
            if (found is null)
            {
                return NotFound(new
                {
                    message = $"Contul @{user.Username} nu mai există în AD sub acest nume. " +
                              "Verificați dacă a fost redenumit sau mutat în afara bazei de căutare.",
                });
            }

            var result = await _provisioner.ApplyAsync(found, user, ct);

            if (result.Changes.Count > 0)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId    = user.Id,
                    Username  = CallerUsername,
                    Action    = AuditAction.DirectoryAccountSynchronized,
                    // Coloana Details are 1024 de caractere; o listă lungă de
                    // modificări ar face SaveChanges să arunce după ce contul a
                    // fost deja actualizat în memorie.
                    Details   = Truncate("Sincronizare manuala din AD: " + string.Join("; ", result.Changes), 1024),
                    IpAddress = CallerIp,
                    Timestamp = DateTime.UtcNow,
                });
            }

            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message = result.Changes.Count == 0
                    ? "Contul era deja la zi."
                    : "Sincronizat: " + string.Join("; ", result.Changes),
                changes = result.Changes,
                blocked = result.Blocked,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private async Task<OrgUnitImportPlan> BuildPlanAsync(
            IReadOnlyList<DirectoryOrgUnit> units, CancellationToken ct)
        {
            var existing = await _context.OrgUnits
                .AsNoTracking()
                .Select(u => new ExistingOrgUnit(u.Id, u.Name, u.DirectoryDn, u.ParentId, (int)u.Type))
                .ToListAsync(ct);

            var ranks = await _context.OrgLevels
                .AsNoTracking()
                .Select(l => l.Rank)
                .ToListAsync(ct);

            return OrgUnitImportPlanner.Plan(units, existing, ranks);
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max];

        public sealed class OrgUnitImportRequest
        {
            /// <summary>Confirmarea explicită a administratorului.</summary>
            public bool Confirm { get; set; }

            /// <summary>
            /// DN-urile alese. Lipsă sau gol = tot ce apare în previzualizare.
            /// </summary>
            public List<string>? DistinguishedNames { get; set; }
        }

        public sealed class LinkAccountRequest
        {
            /// <summary>Numele din AD. Gol = același nume ca al contului local.</summary>
            public string? SamAccountName { get; set; }
        }
    }
}
