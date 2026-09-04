using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MAI.BusinessLogic.Interfaces;

namespace MAI.BusinessLogic.Services
{
    public class CryptoService : ICryptoService
    {
        public async Task<byte[]> EncryptFileAsync(Stream inputStream, string key)
        {
            using var aes = Aes.Create();
            var keyBytes = Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));
            aes.Key = keyBytes;
            aes.GenerateIV();

            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                await inputStream.CopyToAsync(cs);
                await cs.FlushFinalBlockAsync();
            }

            return ms.ToArray();
        }

        public async Task<byte[]> DecryptFileAsync(byte[] encryptedData, string key)
        {
            using var msInput = new MemoryStream(encryptedData);
            byte[] iv = new byte[16];
            await msInput.ReadAsync(iv, 0, 16);

            using var aes = Aes.Create();
            var keyBytes = Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));
            aes.Key = keyBytes;
            aes.IV = iv;

            using var msOutput = new MemoryStream();
            using (var cs = new CryptoStream(msInput, aes.CreateDecryptor(), CryptoStreamMode.Read))
            {
                await cs.CopyToAsync(msOutput);
            }

            return msOutput.ToArray();
        }

        public string CalculateSHA256(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(data);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}