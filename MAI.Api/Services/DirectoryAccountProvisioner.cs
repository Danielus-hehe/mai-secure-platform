using MAI.BusinessLogic.Ldap;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Services
{
    /// <summary>Ce s-a întâmplat cu contul local după o autentificare în domeniu.</summary>
    /// <param name="User">Contul local, creat sau actualizat. NU e salvat încă.</param>
    /// <param name="Created">True dacă abia a fost creat.</param>
    /// <param name="Changes">Modificările aduse din AD, pentru jurnalul de audit.</param>
    /// <param name="Blocked">Motivul pentru care contul nu are voie să intre, dacă e cazul.</param>
    public sealed record DirectoryProvisionResult(
        User? User,
        bool Created,
        IReadOnlyList<string> Changes,
        string? Blocked);

    public interface IDirectoryAccountProvisioner
    {
        /// <summary>
        /// Aduce contul local în acord cu ce spune AD-ul. Nu salvează: apelantul
        /// decide când, ca autentificarea să rămână o singură tranzacție.
        /// </summary>
        Task<DirectoryProvisionResult> ApplyAsync(
            DirectoryUser directoryUser, User? existing, CancellationToken ct);
    }

    /// <summary>
    /// Traduce un cont din Active Directory într-un rând din tabela noastră.
    ///
    /// Principiul: pentru un cont de domeniu, AD-ul este sursa adevărului pentru
    /// identitate (nume, email), drepturi (rolul, din grupuri) și stare
    /// (activ/dezactivat). Aplicația păstrează local doar ce AD-ul nu are:
    /// cheile E2EE, 2FA-ul, sesiunile, jurnalul.
    ///
    /// Ce NU se ia din AD:
    ///   • parola - nu se copiază nici măcar ca hash. PasswordHash rămâne gol,
    ///     deci un cont de domeniu nu se poate deschide „pe scurtătură” cu o
    ///     parolă păstrată la noi, rămasă valabilă după plecarea din instituție;
    ///   • cine conduce o subdiviziune - în AD nu există noțiunea asta, iar
    ///     deducerea ei din atributul manager ar acorda drepturi de distribuție
    ///     pe baza unei presupuneri.
    /// </summary>
    public sealed class DirectoryAccountProvisioner : IDirectoryAccountProvisioner
    {
        private readonly AppDbContext _context;
        private readonly LdapOptions _options;
        private readonly IReadOnlyList<GroupRoleMapping> _mappings;
        private readonly ILogger<DirectoryAccountProvisioner> _logger;

        public DirectoryAccountProvisioner(
            AppDbContext context,
            LdapOptions options,
            ILogger<DirectoryAccountProvisioner> logger)
        {
            _context  = context;
            _options  = options;
            _logger   = logger;
            _mappings = LdapRoleMapper.Parse(options.GroupRoleMappings);
        }

        public async Task<DirectoryProvisionResult> ApplyAsync(
            DirectoryUser directoryUser, User? existing, CancellationToken ct)
        {
            var changes = new List<string>();

            // Căutarea după objectGUID vine prima: e singurul identificator pe
            // care redenumirea contului în AD nu îl schimbă. Fără ea, un
            // „popescu.ion” redenumit „ion.popescu” ar primi un cont local nou,
            // cu chei noi, iar fișierele primite până atunci ar rămâne pe cel vechi.
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.DirectoryObjectId == directoryUser.ObjectId, ct)
                ?? existing;

            var created = false;

            if (user is null)
            {
                if (!_options.AutoCreateUsers)
                {
                    return new DirectoryProvisionResult(null, false, changes,
                        "Contul de domeniu nu are corespondent local, iar crearea automată este " +
                        "dezactivată (LDAP_AUTO_CREATE=false). Administratorul trebuie să lege contul întâi.");
                }

                // Numele de utilizator e unic fără diferență între majuscule și
                // minuscule. Un cont local cu același nume, dar cu parolă la noi,
                // nu se convertește tăcut în cont de domeniu: ar fi o preluare de
                // identitate făcută de oricine reușește să creeze în AD un cont cu
                // numele potrivit.
                var key = directoryUser.SamAccountName.ToLowerInvariant();
                var clash = await _context.Users.AnyAsync(u => u.Username.ToLower() == key, ct);

                if (clash)
                {
                    return new DirectoryProvisionResult(null, false, changes,
                        $"Există deja un cont local cu numele @{directoryUser.SamAccountName}. " +
                        "Legați-l explicit de contul din domeniu din pagina „Active Directory”.");
                }

                user = new User
                {
                    Username       = directoryUser.SamAccountName,
                    AuthProvider   = AuthProvider.Ldap,

                    // Fără hash: verificarea parolei trece exclusiv prin AD.
                    PasswordHash   = string.Empty,

                    // Nu există parolă temporară dată de un administrator, deci
                    // nu există nici motivul pentru care cheile E2EE ar fi blocate.
                    MustChangePassword = false,

                    // Adresa vine din AD, verificată de administratorul domeniului.
                    // Un al doilea circuit de confirmare prin email ar bloca
                    // conturi valide fără să adauge nicio garanție.
                    EmailConfirmed = true,

                    CreatedAt      = DateTime.UtcNow,
                };

                _context.Users.Add(user);
                created = true;
                changes.Add("cont creat din AD");
            }

            // ── Identificatori ────────────────────────────────────────────────
            if (user.AuthProvider != AuthProvider.Ldap)
            {
                user.AuthProvider = AuthProvider.Ldap;
                changes.Add("cont trecut pe autentificare de domeniu");
            }

            if (!string.Equals(user.Username, directoryUser.SamAccountName, StringComparison.OrdinalIgnoreCase))
            {
                var key = directoryUser.SamAccountName.ToLowerInvariant();
                var taken = await _context.Users
                    .AnyAsync(u => u.Id != user.Id && u.Username.ToLower() == key, ct);

                if (taken)
                {
                    // Redenumirea ar încălca indexul unic. Nu e un motiv să
                    // refuzăm autentificarea: contul rămâne cu numele vechi, iar
                    // administratorul primește semnalul în jurnal.
                    _logger.LogWarning(
                        "AD: contul {Old} a fost redenumit in {New}, dar numele nou e deja folosit local.",
                        user.Username, directoryUser.SamAccountName);
                }
                else
                {
                    changes.Add($"nume schimbat din @{user.Username} în @{directoryUser.SamAccountName}");
                    user.Username = directoryUser.SamAccountName;
                }
            }

            if (user.DirectoryObjectId != directoryUser.ObjectId)
                user.DirectoryObjectId = directoryUser.ObjectId;

            if (user.DirectoryDn != directoryUser.DistinguishedName)
                user.DirectoryDn = directoryUser.DistinguishedName;

            user.DirectoryPasswordSetAt = directoryUser.PasswordSetAt;
            user.DirectorySyncedAt      = DateTime.UtcNow;

            // La prima autentificare nu există împachetare anterioară a cheilor;
            // fără reperul acesta, prima schimbare de parolă din AD n-ar putea fi
            // deosebită de „cheile nu au existat niciodată”.
            if (created) user.KeysWrappedAt = null;

            // ── Atribute sincronizate ────────────────────────────────────────
            if (created || _options.SyncOnLogin)
            {
                var fullName = directoryUser.DisplayName?.Trim();
                if (!string.IsNullOrWhiteSpace(fullName) && user.FullName != fullName)
                {
                    changes.Add($"nume complet: „{fullName}”");
                    user.FullName = fullName;
                }

                var email = directoryUser.Email?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(email) &&
                    !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
                {
                    // Adresa poate fi deja folosită de alt cont (indexul unic pe
                    // lower(Email) o cere). Preferăm să nu sincronizăm decât să
                    // eșueze autentificarea la salvare.
                    var taken = await _context.Users
                        .AnyAsync(u => u.Id != user.Id && u.Email.ToLower() == email.ToLower(), ct);

                    if (taken)
                    {
                        _logger.LogWarning("AD: adresa {Email} e deja folosita local; nu se sincronizeaza.", email);
                    }
                    else
                    {
                        changes.Add($"email: {email}");
                        user.Email = email;
                    }
                }

                var role = LdapRoleMapper.Resolve(directoryUser.Groups, _mappings, _options.DefaultRoleValue);
                if (user.Role != role)
                {
                    changes.Add($"rol: {user.Role} → {role} (din grupurile AD)");
                    user.Role = role;
                }

                var unitId = await ResolveOrgUnitAsync(directoryUser.Department, ct);
                if (unitId.HasValue && user.OrgUnitId != unitId)
                {
                    changes.Add($"subdiviziune după atributul „{_options.DepartmentAttribute}”");
                    user.OrgUnitId = unitId;
                }

                // Dezactivarea în AD trebuie să se vadă imediat la noi: altfel un
                // cont închis la plecarea din instituție ar rămâne cu sesiuni
                // valabile și cu drepturi în aplicație.
                if (user.IsActive != directoryUser.Enabled)
                {
                    changes.Add(directoryUser.Enabled ? "cont reactivat din AD" : "cont dezactivat în AD");
                    user.IsActive = directoryUser.Enabled;
                }
            }

            if (!user.IsActive)
            {
                return new DirectoryProvisionResult(user, created, changes,
                    "Contul este dezactivat în Active Directory.");
            }

            return new DirectoryProvisionResult(user, created, changes, null);
        }

        /// <summary>
        /// Caută subdiviziunea după valoarea atributului din AD, comparând cu
        /// denumirea și cu prescurtarea. Dacă nu se potrivește exact una singură,
        /// contul rămâne neîncadrat: o subdiviziune ghicită greșit ar trimite
        /// documentele interne altor oameni decât trebuie.
        /// </summary>
        private async Task<Guid?> ResolveOrgUnitAsync(string? department, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(department)) return null;

            var value = department.Trim();

            var matches = await _context.OrgUnits
                .AsNoTracking()
                .Where(u => u.IsActive &&
                            (u.Name.ToLower() == value.ToLower() ||
                             (u.Code != null && u.Code.ToLower() == value.ToLower())))
                .Select(u => u.Id)
                .Take(2)
                .ToListAsync(ct);

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
            {
                _logger.LogInformation(
                    "AD: atributul departament „{Department}” nu corespunde niciunei subdiviziuni. " +
                    "Contul ramane neincadrat.", value);
            }

            return null;
        }
    }
}
