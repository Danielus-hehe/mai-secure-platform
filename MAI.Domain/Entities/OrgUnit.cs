using System;
using System.Collections.Generic;
using MAI.Domain.Enums;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// O subdiviziune a instituției: Direcție, Secție sau Serviciu.
    ///
    /// Înlocuiește câmpul text liber User.Department. Textul liber nu putea
    /// răspunde la întrebările de care are nevoie distribuția documentelor
    /// interne — „cine e în subordinea mea?”, „cine conduce secția asta?” —
    /// iar „Directia IT” și „Direcția Tehnologii Informaționale” erau două
    /// departamente diferite pentru server.
    ///
    /// ȘEFUL este definit de unitatea condusă (HeadUserId), nu de rol. Rolul
    /// SefDirectie rămâne o permisiune de supervizare (jurnal de audit); cine
    /// conduce efectiv o subdiviziune se vede aici. Un șef de serviciu are rolul
    /// Utilizator și totuși distribuie documente subordonaților lui.
    /// </summary>
    public class OrgUnit
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Denumirea completă, ex. „Direcția Tehnologii Informaționale”.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Prescurtare opțională, ex. „DTI”. Afișată în liste înguste.</summary>
        public string? Code { get; set; }

        public OrgUnitType Type { get; set; } = OrgUnitType.Directie;

        /// <summary>Subdiviziunea-părinte. Null = nivel de vârf (direct sub conducerea instituției).</summary>
        public Guid? ParentId { get; set; }
        public OrgUnit? Parent { get; set; }
        public ICollection<OrgUnit> Children { get; set; } = [];

        /// <summary>
        /// Conducătorul subdiviziunii. Un utilizator conduce cel mult o
        /// subdiviziune (index unic filtrat).
        /// </summary>
        public Guid? HeadUserId { get; set; }
        public User? HeadUser { get; set; }

        /// <summary>
        /// O subdiviziune desființată nu se șterge dacă e referită de documente
        /// distribuite: se dezactivează. Nu mai apare la alegerea destinatarilor
        /// și nu mai primește membri noi.
        /// </summary>
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
