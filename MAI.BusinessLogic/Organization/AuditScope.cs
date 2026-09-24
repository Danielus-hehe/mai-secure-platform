namespace MAI.BusinessLogic.Organization
{
    /// <summary>
    /// Ce parte din jurnalul de audit vede un șef de direcție.
    ///
    /// Înainte, rolul SefDirectie vedea tot jurnalul ministerului: autentificările
    /// oricui, adresele IP, numele fișierelor trimise de alte direcții, acțiunile
    /// administratorilor. Rolul îi dă dreptul să supravegheze subdiviziunea pe
    /// care o conduce, nu întreaga instituție. Principiul privilegiului minim,
    /// aplicat și la citire.
    ///
    /// Vizibil pentru șef: propriile acțiuni și ale tuturor membrilor din
    /// subdiviziunea condusă și din subdiviziunile ei, inclusiv ale celor
    /// dezactivați (istoricul unui om plecat rămâne relevant). Rândurile fără
    /// utilizator (încercări de login cu nume inexistente, cereri anonime) nu
    /// aparțin nimănui din subdiviziune și rămân doar la administrator.
    ///
    /// Funcție pură: structura și membrii vin ca parametri, deci regula se
    /// testează fără bază de date, exact ca DistributionResolver.
    /// </summary>
    public static class AuditScope
    {
        public static IReadOnlySet<Guid> VisibleUserIds(OrgTree tree, IEnumerable<OrgMember> members, Guid viewerId)
        {
            var visible = new HashSet<Guid> { viewerId };

            // Un șef fără subdiviziune condusă (încă neîncadrat, sau unitatea lui
            // a fost dezactivată) își vede doar propriile acțiuni.
            var led = tree.LedBy(viewerId);
            if (led is null) return visible;

            var units = tree.Subtree(led.Id).Select(u => u.Id).ToHashSet();

            foreach (var member in members)
            {
                if (member.OrgUnitId is Guid unit && units.Contains(unit))
                    visible.Add(member.Id);
            }

            return visible;
        }
    }
}
