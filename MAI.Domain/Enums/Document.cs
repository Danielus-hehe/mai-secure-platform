using System;
using System.Collections.Generic;

namespace MAI.Domain.Entities
{
    public class Document
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string DocumentNumber { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        
        public string Keywords { get; set; } = string.Empty;

        public int CurrentVersion { get; set; } = 1;

        /// <summary>
        /// Token de concurență (coloana de sistem xmin). O versiune nouă
        /// calculează CurrentVersion + 1; două încărcări simultane obțineau
        /// același număr și a doua suprascria obiectul primei în depozit.
        /// Acum a doua salvare eșuează cu 409, iar obiectul ei (cu cheie
        /// proprie) se retrage fără să-l atingă pe al primei.
        /// </summary>
        public uint Version { get; set; }
        public Guid CreatedById { get; set; }
        public User? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public ICollection<DocumentVersion> Versions { get; set; } = new List<DocumentVersion>();
    }
}