using System;

namespace MAI.Domain.Entities
{
    public class DocumentVersion
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid DocumentId { get; set; }
        public Document? Document { get; set; }
        public int VersionNumber { get; set; }
        public string EncryptedStoragePath { get; set; } = string.Empty;
        public string ChecksumSHA256 { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string ChangeNotes { get; set; } = string.Empty;
    }
}