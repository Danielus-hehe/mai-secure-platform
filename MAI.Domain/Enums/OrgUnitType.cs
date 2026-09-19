namespace MAI.Domain.Enums
{
    /// <summary>
    /// Nivelul unei subdiviziuni în structura MAI. Persistat ca int.
    ///
    /// Ordinea contează: un copil are întotdeauna un nivel mai mare decât
    /// părintele (o Secție stă sub o Direcție, nu invers). Regula e verificată
    /// de OrgTree.ValidatePlacement.
    /// </summary>
    public enum OrgUnitType
    {
        Directie = 1,
        Sectie   = 2,
        Serviciu = 3,
    }
}
