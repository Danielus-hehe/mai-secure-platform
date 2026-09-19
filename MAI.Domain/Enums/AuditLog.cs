using System;
using MAI.Domain.Enums;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// O intrare în jurnalul de audit. Scriere unică: nimic din aplicație nu
    /// actualizează sau șterge rânduri de aici.
    /// </summary>
    public class AuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public AuditAction Action { get; set; }

        /// <summary>
        /// Descrierea în clar a operației, fără prefix de rezultat. Rezultatul
        /// se citește din <see cref="Result"/>, nu din primele caractere ale
        /// acestui câmp.
        /// </summary>
        public string Details { get; set; } = string.Empty;

        /// <summary>
        /// Rezultatul operației. Implicit <see cref="AuditResult.Success"/>, ca
        /// apelantul să fie nevoit să spună explicit doar când ceva a mers prost -
        /// cazul rar, deci cel care merită să sară în ochi la citirea codului.
        /// </summary>
        public AuditResult Result { get; set; } = AuditResult.Success;

        public string IpAddress { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
