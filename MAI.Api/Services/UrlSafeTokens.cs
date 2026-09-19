using System.Security.Cryptography;
using System.Text;

namespace MAI.Api.Services
{
    /// <summary>
    /// Tokenuri opace pentru linkuri trimise pe email (invitație, resetare).
    /// Tokenul brut ajunge doar în email; în bază se ține SHA-256 al lui, ca o
    /// copie a bazei să nu permită folosirea linkurilor încă valabile.
    /// </summary>
    public static class UrlSafeTokens
    {
        /// <summary>256 de biți aleatorii, Base64Url fără padding.</summary>
        public static string Generate() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

        /// <summary>SHA-256 hex (minuscule) - forma stocată în bază.</summary>
        public static string Hash(string raw) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
