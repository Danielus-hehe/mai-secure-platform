namespace MAI.Domain.Enums
{
    /// <summary>
    /// Nivelul unei subdiviziuni, ca rang numeric. Persistat ca int.
    ///
    /// Membrii de aici sunt doar nivelurile predefinite. Administratorul poate
    /// adăuga niveluri proprii (vezi OrgLevel) între ele sau sub ele, de aceea
    /// rangurile au goluri de câte 100: un „Departament” între Direcție și
    /// Secție primește rangul 150, un „Birou” sub Serviciu primește 400.
    ///
    /// Regula de structură rămâne una singură: un copil are rangul strict mai
    /// mare decât părintele (verificată de OrgTree.ValidatePlacement).
    /// </summary>
    public enum OrgUnitType
    {
        Directie = 100,
        Sectie   = 200,
        Serviciu = 300,
    }
}
