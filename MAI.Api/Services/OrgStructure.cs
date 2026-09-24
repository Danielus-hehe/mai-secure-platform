using System.Security.Claims;
using MAI.BusinessLogic.Organization;
using MAI.DataAccessLayer;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Services
{
    /// <summary>
    /// Încărcarea structurii organizatorice pentru regulile din
    /// MAI.BusinessLogic.Organization. O interogare pentru tot arborele,
    /// o interogare pentru toți membrii - apoi totul se calculează în memorie.
    /// </summary>
    public static class OrgStructure
    {
        public static async Task<OrgTree> LoadTreeAsync(AppDbContext db, CancellationToken ct) =>
            new(await db.OrgUnits
                .AsNoTracking()
                .Select(u => new OrgUnitNode(u.Id, u.ParentId, u.HeadUserId, u.Type, u.IsActive, u.Name))
                .ToListAsync(ct));

        public static async Task<List<OrgMember>> LoadMembersAsync(AppDbContext db, CancellationToken ct) =>
            await db.Users
                .AsNoTracking()
                .Select(u => new OrgMember(u.Id, u.OrgUnitId, u.IsActive))
                .ToListAsync(ct);

        /// <summary>
        /// Utilizatorii ale căror rânduri de audit le poate citi apelantul, sau
        /// null pentru „toate” (Administrator). Folosit de jurnal, export,
        /// lista de nume și alertele de securitate, ca toate să aplice aceeași
        /// regulă: un șef vede subdiviziunea lui, nu ministerul. Vezi AuditScope.
        /// </summary>
        public static async Task<Guid[]?> AuditVisibleUsersAsync(
            AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
        {
            if (user.IsInRole(nameof(UserRole.Administrator)))
                return null;

            // Fără identificator valid nu se vede nimic, nu „tot”: eșecul
            // trebuie să restrângă, nu să lărgească.
            if (!Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var viewerId))
                return Array.Empty<Guid>();

            var tree    = await LoadTreeAsync(db, ct);
            var members = await LoadMembersAsync(db, ct);

            return AuditScope.VisibleUserIds(tree, members, viewerId).ToArray();
        }
    }
}
