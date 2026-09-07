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
        public Guid CreatedById { get; set; }
        public User? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public ICollection<DocumentVersion> Versions { get; set; } = new List<DocumentVersion>();
    }
}