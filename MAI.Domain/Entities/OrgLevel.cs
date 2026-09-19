using System;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// Un nivel al structurii organizatorice, cu denumirea lui.
    ///
    /// Direcție / Secție / Serviciu sunt create de migrare, dar nu sunt fixe:
    /// administratorul le poate redenumi și poate adăuga altele („Departament”,
    /// „Inspectorat”, „Birou”, „Grup”), ca structura din SGDM să urmeze
    /// organigrama reală, nu una impusă de aplicație.
    ///
    /// Cheia este rangul: OrgUnit.Type conține rangul nivelului. Un rang mai mic
    /// înseamnă un nivel mai înalt în ierarhie.
    /// </summary>
    public class OrgLevel
    {
        /// <summary>Rangul (1-10000). Direcție = 100, Secție = 200, Serviciu = 300.</summary>
        public int Rank { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
