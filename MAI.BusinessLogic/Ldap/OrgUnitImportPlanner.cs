using System;
using System.Collections.Generic;
using System.Linq;
using MAI.BusinessLogic.Organization;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>Ce se întâmplă cu o unitate organizatorică la import.</summary>
    public enum OrgUnitImportAction
    {
        /// <summary>Nu există la noi: se creează o subdiviziune nouă.</summary>
        Create = 0,

        /// <summary>Există și se modifică (denumire, părinte, nivel sau legătura cu AD).</summary>
        Update = 1,

        /// <summary>Există și corespunde deja: nu se atinge.</summary>
        Unchanged = 2,
    }

    /// <summary>O linie din planul de import, așa cum o vede administratorul înainte de a confirma.</summary>
    public sealed record OrgUnitImportItem(
        string DistinguishedName,
        string Name,
        string? ParentDn,
        int Depth,
        int Rank,
        OrgUnitImportAction Action,
        Guid? ExistingId,
        string? Note);

    /// <summary>
    /// Planul complet. Se calculează și se arată; nimic nu se scrie în bază
    /// până când administratorul nu îl confirmă.
    /// </summary>
    public sealed record OrgUnitImportPlan(
        IReadOnlyList<OrgUnitImportItem> Items,
        IReadOnlyList<int> MissingLevelRanks,
        IReadOnlyList<string> Warnings)
    {
        public int ToCreate => Items.Count(i => i.Action == OrgUnitImportAction.Create);
        public int ToUpdate => Items.Count(i => i.Action == OrgUnitImportAction.Update);
        public int Unchanged => Items.Count(i => i.Action == OrgUnitImportAction.Unchanged);
    }

    /// <summary>Subdiviziunea existentă, redusă la ce contează pentru planificare.</summary>
    public sealed record ExistingOrgUnit(
        Guid Id,
        string Name,
        string? DirectoryDn,
        Guid? ParentId,
        int Rank);

    /// <summary>
    /// Traduce arborele de unități organizatorice din AD într-un plan de
    /// modificări asupra subdiviziunilor noastre.
    ///
    /// De ce plan și nu import direct: structura unei instituții de forță nu se
    /// rescrie automat pe baza a ce se întâmplă să conțină AD-ul. Un OU redenumit
    /// sau șters din greșeală în domeniu ar rupe distribuția documentelor
    /// interne. Administratorul vede exact ce s-ar schimba și confirmă.
    ///
    /// Ce NU face importul, deliberat:
    ///   • nu șterge și nu dezactivează subdiviziuni care nu mai apar în AD -
    ///     ele pot avea documente distribuite și oameni încadrați;
    ///   • nu mută utilizatori dintr-o subdiviziune în alta;
    ///   • nu atinge șefii (HeadUserId): în AD nu există noțiunea de „șef al
    ///     unui OU”, iar ghicirea ei din atributul manager ar da drepturi de
    ///     distribuție pe baza unei presupuneri.
    ///
    /// Clasa e pură: nu vede EF, deci se testează fără bază de date.
    /// </summary>
    public static class OrgUnitImportPlanner
    {
        /// <param name="directoryUnits">OU-urile citite din AD.</param>
        /// <param name="existing">Subdiviziunile noastre de acum.</param>
        /// <param name="levelRanks">Rangurile nivelurilor configurate (OrgLevels), crescător.</param>
        public static OrgUnitImportPlan Plan(
            IReadOnlyList<DirectoryOrgUnit> directoryUnits,
            IReadOnlyList<ExistingOrgUnit> existing,
            IReadOnlyList<int> levelRanks)
        {
            var warnings = new List<string>();
            var items    = new List<OrgUnitImportItem>();

            var ranks = levelRanks.Distinct().OrderBy(r => r).ToList();
            if (ranks.Count == 0) ranks.Add((int)Domain.Enums.OrgUnitType.Directie);

            var byDn = existing
                .Where(u => !string.IsNullOrWhiteSpace(u.DirectoryDn))
                .GroupBy(u => Canonical(u.DirectoryDn!), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Potrivirea după denumire există pentru prima rulare, când
            // structura a fost deja introdusă manual: altfel importul ar dubla
            // fiecare direcție, iar utilizatorii ar rămâne încadrați în copia
            // veche, fără să apară în distribuția documentelor.
            var byName = existing
                .GroupBy(u => u.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var missingRanks = new SortedSet<int>();

            // Ordinea pe adâncime garantează că părintele e planificat înaintea
            // copilului: la aplicare, ID-ul părintelui e deja cunoscut.
            foreach (var unit in directoryUnits.OrderBy(u => u.Depth).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
            {
                var rank = RankForDepth(ranks, unit.Depth);
                if (!ranks.Contains(rank)) missingRanks.Add(rank);

                var dnKey = Canonical(unit.DistinguishedName);

                if (byDn.TryGetValue(dnKey, out var linked))
                {
                    var changed = !string.Equals(linked.Name, unit.Name, StringComparison.Ordinal)
                                  || linked.Rank != rank;

                    items.Add(new OrgUnitImportItem(
                        unit.DistinguishedName, unit.Name, unit.ParentDn, unit.Depth, rank,
                        changed ? OrgUnitImportAction.Update : OrgUnitImportAction.Unchanged,
                        linked.Id,
                        changed ? $"Se actualizează din „{linked.Name}”." : null));
                    continue;
                }

                if (byName.TryGetValue(unit.Name.Trim(), out var sameName))
                {
                    items.Add(new OrgUnitImportItem(
                        unit.DistinguishedName, unit.Name, unit.ParentDn, unit.Depth, rank,
                        OrgUnitImportAction.Update, sameName.Id,
                        "Subdiviziune existentă cu aceeași denumire: se leagă de OU-ul din AD, nu se creează a doua."));
                    continue;
                }

                items.Add(new OrgUnitImportItem(
                    unit.DistinguishedName, unit.Name, unit.ParentDn, unit.Depth, rank,
                    OrgUnitImportAction.Create, null, null));
            }

            if (missingRanks.Count > 0)
            {
                warnings.Add(
                    "Structura din AD are mai multe niveluri decât cele configurate. " +
                    "Se vor crea nivelurile cu rangurile: " + string.Join(", ", missingRanks) + ".");
            }

            // Subdiviziuni legate de AD care nu mai apar în rezultat: se
            // semnalează, dar nu se ating. Un OU mutat în afara bazei de căutare
            // arată identic cu unul șters, iar ștergerea automată ar face
            // documentele distribuite acolo să rămână fără destinatari.
            var seen = new HashSet<string>(items.Select(i => Canonical(i.DistinguishedName)), StringComparer.OrdinalIgnoreCase);
            var orphans = existing
                .Where(u => !string.IsNullOrWhiteSpace(u.DirectoryDn) && !seen.Contains(Canonical(u.DirectoryDn!)))
                .Select(u => u.Name)
                .ToList();

            if (orphans.Count > 0)
            {
                warnings.Add(
                    "Subdiviziuni importate anterior care nu mai apar în AD (NU se șterg, verificați manual): " +
                    string.Join(", ", orphans) + ".");
            }

            return new OrgUnitImportPlan(items, missingRanks.ToList(), warnings);
        }

        /// <summary>
        /// Rangul nivelului pentru o adâncime din AD. Sub ultimul nivel
        /// configurat se continuă cu pasul standard, ca un copil să rămână mereu
        /// pe un rang strict mai mare decât părintele (regula din OrgTree).
        /// </summary>
        public static int RankForDepth(IReadOnlyList<int> ranks, int depth)
        {
            if (depth < ranks.Count) return ranks[depth];

            var last = ranks[^1];
            return last + (depth - ranks.Count + 1) * OrgLevelRules.Step;
        }

        private static string Canonical(string dn) =>
            string.Join(',', dn.Split(',').Select(p => p.Trim()));
    }
}
