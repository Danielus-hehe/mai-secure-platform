using System;
using System.IO;
using System.Threading.Tasks;
using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.BusinessLogic.Services
{
    public class TransferService
    {
        private readonly AppDbContext _db;
        private readonly ICryptoService _crypto;
        private readonly string _storagePath = Path.Combine(Directory.GetCurrentDirectory(), "Storage", "Transfers");
        private const string EncryptionSecret = "MAI_Intranet_Secret_Key_2026";

        public TransferService(AppDbContext db, ICryptoService crypto)
        {
            _db = db;
            _crypto = crypto;
            if (!Directory.Exists(_storagePath)) Directory.CreateDirectory(_storagePath);
        }

        public async Task<FileTransfer> SendFileAsync(Guid senderId, Guid recipientId, Stream fileStream, string fileName, string ip)
        {
            using var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms);
            var fileBytes = ms.ToArray();

            var checksum = _crypto.CalculateSHA256(fileBytes);
            ms.Position = 0;
            var encryptedData = await _crypto.EncryptFileAsync(ms, EncryptionSecret);

            var fileId = Guid.NewGuid();
            var filePath = Path.Combine(_storagePath, $"{fileId}.dat");
            await File.WriteAllBytesAsync(filePath, encryptedData);

            var transfer = new FileTransfer
            {
                Id = fileId,
                SenderId = senderId,
                RecipientId = recipientId,
                FileName = fileName,
                EncryptedStoragePath = filePath,
                FileSize = fileBytes.Length,
                ChecksumSHA256 = checksum,
                Status = TransferStatus.Pending
            };

            _db.FileTransfers.Add(transfer);
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = senderId,
                Action = AuditAction.FileUpload,
                Details = $"Fișier trimis: {fileName} către {recipientId}",
                IpAddress = ip
            });

            await _db.SaveChangesAsync();
            return transfer;
        }

        public async Task<(byte[] fileContent, string fileName)> DownloadFileAsync(Guid transferId, Guid userId, string ip)
        {
            var transfer = await _db.FileTransfers.FirstOrDefaultAsync(t => t.Id == transferId && t.RecipientId == userId)
                ?? throw new KeyNotFoundException("Transferul nu există sau nu aveți acces.");

            var encryptedBytes = await File.ReadAllBytesAsync(transfer.EncryptedStoragePath);
            var decryptedBytes = await _crypto.DecryptFileAsync(encryptedBytes, EncryptionSecret);

            var currentChecksum = _crypto.CalculateSHA256(decryptedBytes);
            if (currentChecksum != transfer.ChecksumSHA256)
            {
                throw new InvalidOperationException("Integritate compromisă!");
            }

            transfer.Status = TransferStatus.Downloaded;
            transfer.DownloadedAt = DateTime.UtcNow;

            _db.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                Action = AuditAction.FileDownload,
                Details = $"Descărcare reușită (SHA-256 ok): {transfer.FileName}",
                IpAddress = ip
            });

            await _db.SaveChangesAsync();
            return (decryptedBytes, transfer.FileName);
        }
    }
}