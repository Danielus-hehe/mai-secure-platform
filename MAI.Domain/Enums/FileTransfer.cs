using System;
using MAI.Domain.Enums;

namespace MAI.Domain.Entities
{
    public class FileTransfer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid SenderId { get; set; }
        public User? Sender { get; set; }
        public Guid RecipientId { get; set; }
        public User? Recipient { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string EncryptedStoragePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string ChecksumSHA256 { get; set; } = string.Empty;
        public TransferStatus Status { get; set; } = TransferStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DownloadedAt { get; set; }
    }
}