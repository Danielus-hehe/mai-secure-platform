using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MAI.BusinessLogic.Dtos;

namespace MAI.BusinessLogic.Interfaces
{
    public interface ITransferService
    {
        Task<FileTransferDto> SendFileAsync(Guid senderId, Guid recipientId, Stream fileStream, string fileName, string ip);
        Task<(byte[] fileContent, string fileName)> DownloadFileAsync(Guid transferId, Guid userId, string ip);
        Task<IEnumerable<FileTransferDto>> GetUserTransfersAsync(Guid userId);
    }
}