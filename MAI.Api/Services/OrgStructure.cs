using MAI.BusinessLogic.Organization;
using MAI.DataAccessLayer;
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
    }
}
