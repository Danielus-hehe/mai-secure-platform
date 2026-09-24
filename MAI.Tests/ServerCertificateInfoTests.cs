using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MAI.BusinessLogic.Ldap;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Datele certificatului afișate la testul de conexiune AD.
///
/// Regresia reparată: serviciul păstra obiectul certificatului primit în
/// callbackul TLS. SslStream îl elibera la închiderea conexiunii, iar citirea
/// lui .Subject arunca apoi CryptographicException, chiar din blocul catch al
/// testului de conexiune. Rezultatul era un 500 fără mesaj în pagina Active
/// Directory, în locul erorii reale (DC oprit, certificat respins etc.).
/// </summary>
public class ServerCertificateInfoTests
{
    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Datele_RamanValabile_DupaEliberareaCertificatului()
    {
        var certificate = SelfSigned("CN=dc1.sgdm.local");
        var expectedThumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        var info = ServerCertificateInfo.From(certificate);
        certificate.Dispose();

        // Exact ce făcea blocul catch: citire după eliberare. Acum sunt șiruri.
        Assert.Equal("CN=dc1.sgdm.local", info.Subject);
        Assert.Equal(expectedThumbprint, info.Thumbprint);
    }

    [Fact]
    public void Amprenta_EsteSha256_FaraSeparatori_CaInLdapCertThumbprint()
    {
        using var certificate = SelfSigned("CN=dc1.sgdm.local");

        var info = ServerCertificateInfo.From(certificate);

        Assert.Equal(64, info.Thumbprint.Length);
        Assert.DoesNotContain(":", info.Thumbprint);
    }

    [Fact]
    public void AcceptaSiTipulDeBaza_X509Certificate()
    {
        // Unele platforme dau callbackului un X509Certificate simplu, nu X509Certificate2.
        using var certificate = SelfSigned("CN=dc1.sgdm.local");
        using var plain = new X509Certificate(certificate.RawData);

        Assert.Equal("CN=dc1.sgdm.local", ServerCertificateInfo.From(plain).Subject);
    }
}
