using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Organization
{
    /// <summary>Utilizatorul, redus la ce contează pentru distribuție.</summary>
    public sealed record OrgMember(Guid Id, Guid? OrgUnitId, bool IsActive);

    /// <summary>Cererea de distribuție, așa cum vine de la autor.</summary>
    public sealed record DistributionRequest(
        DistributionMode Mode,
        IReadOnlyCollection<Guid> UnitIds,
        IReadOnlyCollection<Guid> UserIds,
        bool IncludeSubunits);

    public sealed record DistributionResult(
        bool Success,
        IReadOnlyList<Guid> RecipientIds,
        string? Error)
    {
        public static DistributionResult Ok(IEnumerable<Guid> ids) => new(true, ids.Distinct().ToList(), null);
        public static DistributionResult Fail(string error) => new(false, [], error);
    }

    /// <summary>Ce moduri de distribuție are la dispoziție un autor.</summary>
    public sealed record DistributionCapabilities(
        Guid? LedUnitId,
        bool IsAdmin,
        IReadOnlyList<DistributionMode> Modes,
        IReadOnlyList<Guid> SelectableUnitIds);

    /// <summary>
    /// Transformă „cui se distribuie” în lista concretă de destinatari și
    /// verifică dreptul autorului de a face acea distribuție.
    ///
    /// Regulile, pe scurt:
    ///   • cine conduce o subdiviziune distribuie în jos, în arborele ei;
    ///   • oricine poate adresa un document unor persoane anume;
    ///   • doar administratorul distribuie în toată instituția sau în
    ///     subdiviziuni pe care nu le conduce.
    ///
    /// Funcție pură: primește structura și utilizatorii, nu atinge baza de date.
    /// Controller-ul încarcă datele, apelează Resolve și abia apoi scrie.
    /// </summary>
    public static class DistributionResolver
    {
        public static DistributionCapabilities CapabilitiesFor(Guid authorId, bool isAdmin, OrgTree tree)
        {
            var led   = tree.LedBy(authorId);
            var modes = new List<DistributionMode>();

            if (led is not null)
            {
                modes.Add(DistributionMode.MyUnitTree);
                modes.Add(DistributionMode.DirectSubordinates);
                modes.Add(DistributionMode.SelectedUnits);
                if (tree.ChildrenOf(led.Id).Count > 0)
                    modes.Add(DistributionMode.UnitHeads);
            }
            else if (isAdmin)
            {
                modes.Add(DistributionMode.SelectedUnits);
                modes.Add(DistributionMode.UnitHeads);
            }

            modes.Add(DistributionMode.SpecificUsers);
            if (isAdmin) modes.Add(DistributionMode.WholeInstitution);

            var selectable = isAdmin
                ? tree.All.Where(u => u.IsActive).Select(u => u.Id).ToList()
                : led is not null
                    ? tree.Subtree(led.Id).Where(u => u.IsActive).Select(u => u.Id).ToList()
                    : [];

            return new DistributionCapabilities(led?.Id, isAdmin, modes.Distinct().ToList(), selectable);
        }

        public static DistributionResult Resolve(
            Guid authorId,
            bool isAdmin,
            DistributionRequest request,
            OrgTree tree,
            IReadOnlyCollection<OrgMember> members)
        {
            if (!Enum.IsDefined(request.Mode))
                return DistributionResult.Fail("Modul de distribuție nu există.");

            var caps = CapabilitiesFor(authorId, isAdmin, tree);
            if (!caps.Modes.Contains(request.Mode))
                return DistributionResult.Fail(request.Mode switch
                {
                    DistributionMode.WholeInstitution =>
                        "Doar administratorul poate distribui un document în toată instituția.",
                    DistributionMode.MyUnitTree or DistributionMode.DirectSubordinates or DistributionMode.UnitHeads =>
                        "Nu conduceți nicio subdiviziune, deci nu aveți subordonați cărora să le distribuiți.",
                    _ => "Nu aveți dreptul la acest mod de distribuție.",
                });

            var active = members.Where(m => m.IsActive && m.Id != authorId).ToList();

            IEnumerable<Guid> MembersOf(IEnumerable<Guid> unitIds)
            {
                var set = unitIds.ToHashSet();
                return active.Where(m => m.OrgUnitId.HasValue && set.Contains(m.OrgUnitId.Value)).Select(m => m.Id);
            }

            IEnumerable<Guid> ActiveHeads(IEnumerable<OrgUnitNode> units)
            {
                var activeIds = active.Select(m => m.Id).ToHashSet();
                return units
                    .Where(u => u.IsActive && u.HeadUserId.HasValue && activeIds.Contains(u.HeadUserId.Value))
                    .Select(u => u.HeadUserId!.Value);
            }

            var led = caps.LedUnitId is { } ledId ? tree.Find(ledId) : null;

            IEnumerable<Guid> recipients;
            switch (request.Mode)
            {
                case DistributionMode.MyUnitTree:
                    recipients = MembersOf(tree.Subtree(led!.Id).Select(u => u.Id))
                        .Concat(ActiveHeads(tree.Subtree(led.Id, includeRoot: false)));
                    break;

                case DistributionMode.DirectSubordinates:
                    // Membrii subdiviziunii conduse (fără cei din subunități) și
                    // șefii subunităților imediat inferioare - organigrama clasică.
                    recipients = MembersOf([led!.Id])
                        .Concat(ActiveHeads(tree.ChildrenOf(led.Id)));
                    break;

                case DistributionMode.SelectedUnits:
                {
                    if (request.UnitIds.Count == 0)
                        return DistributionResult.Fail("Alegeți cel puțin o subdiviziune.");

                    var selectable = caps.SelectableUnitIds.ToHashSet();
                    var outside = request.UnitIds.FirstOrDefault(id => !selectable.Contains(id));
                    if (outside != Guid.Empty)
                        return DistributionResult.Fail(tree.Find(outside) is null
                            ? "Una dintre subdiviziunile alese nu există sau este desființată."
                            : $"Subdiviziunea „{tree.Find(outside)!.Name}” nu este în subordinea dumneavoastră.");

                    var units = request.UnitIds
                        .SelectMany(id => request.IncludeSubunits ? tree.Subtree(id) : [tree.Find(id)!])
                        .DistinctBy(u => u.Id)
                        .ToList();

                    recipients = MembersOf(units.Select(u => u.Id)).Concat(ActiveHeads(units));
                    break;
                }

                case DistributionMode.UnitHeads:
                {
                    var units = led is not null
                        ? tree.Subtree(led.Id, includeRoot: false)
                        : tree.All.ToList();   // administrator fără subdiviziune condusă
                    recipients = ActiveHeads(units);
                    break;
                }

                case DistributionMode.SpecificUsers:
                {
                    if (request.UserIds.Count == 0)
                        return DistributionResult.Fail("Alegeți cel puțin o persoană.");

                    var activeIds = active.Select(m => m.Id).ToHashSet();
                    if (request.UserIds.Any(id => id == authorId))
                        return DistributionResult.Fail("Nu vă puteți adresa documentul dumneavoastră înșivă.");
                    if (request.UserIds.Any(id => !activeIds.Contains(id)))
                        return DistributionResult.Fail("Una dintre persoanele alese nu există sau are contul dezactivat.");

                    recipients = request.UserIds;
                    break;
                }

                case DistributionMode.WholeInstitution:
                    recipients = active.Select(m => m.Id);
                    break;

                default:
                    return DistributionResult.Fail("Modul de distribuție nu există.");
            }

            var list = recipients.Where(id => id != authorId).Distinct().ToList();
            return list.Count == 0
                ? DistributionResult.Fail("Distribuția aleasă nu cuprinde niciun destinatar activ.")
                : DistributionResult.Ok(list);
        }
    }
}
