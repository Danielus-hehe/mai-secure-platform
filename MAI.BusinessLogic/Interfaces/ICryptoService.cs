using System.IO;
using System.Threading.Tasks;

namespace MAI.BusinessLogic.Interfaces
{
    public interface ICryptoService
    {
        Task<byte[]> EncryptFileAsync(Stream inputStream, string key);
        Task<byte[]> DecryptFileAsync(byte[] encryptedData, string key);
        string CalculateSHA256(byte[] data);
    }
}