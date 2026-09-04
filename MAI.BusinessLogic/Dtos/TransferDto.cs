using System;
using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Dtos
{
    public class FileTransferDto
    {
        public Guid Id { get; set; }
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public Guid RecipientId { get; set; }
        public string RecipientName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string ChecksumSHA256 { get; set; } = string.Empty;
        public TransferStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? DownloadedAt { get; set; }
    }
}