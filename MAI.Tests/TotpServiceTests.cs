using MAI.BusinessLogic.Security;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Teste pe implementarea TOTP (RFC 6238), scrisă de mână în proiect.
///
/// Vectorii de test din RFC 4226, Anexa D, sunt cel mai important test din
/// fișier: implementarea nu trebuie doar să fie autoconsistentă, ci să producă
/// exact codurile pe care le va afișa telefonul utilizatorului. Un algoritm
/// greșit dar consistent cu sine ar trece toate celelalte teste și ar eșua în
/// producție, la prima înrolare.
/// </summary>
public class TotpServiceTests
{
    private static TwoFactorOptions Options(int digits = 6, int window = 1) => new()
    {
        Issuer                   = "SGDM MAI",
        Digits                   = digits,
        PeriodSeconds            = 30,
        WindowSteps              = window,
        RecoveryCodeCount        = 10,
        ChallengeLifetimeSeconds = 180,
        MaxChallengeAttempts     = 5,
        EncryptionKey            = "dGVzdC1jaGVpZS1kZS0zMi1vY3RldGktcGVudHJ1LXRlc3Rl",
    };

    private static TotpService Service(int digits = 6, int window = 1) => new(Options(digits, window));

    // ── Vectorii oficiali RFC 4226 ───────────────────────────────────────────
    // Secret: ASCII "12345678901234567890", contoarele 0..9.
    private static readonly byte[] RfcSecret =
        System.Text.Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void ComputeCode_CorespundeVectorilorDinRfc4226(long counter, string expected)
    {
        Assert.Equal(expected, Service().ComputeCode(RfcSecret, counter));
    }

    [Fact]
    public void GenerateSecret_ProduceBase32Unic()
    {
        var service = Service();

        var a = service.GenerateSecret();
        var b = service.GenerateSecret();

        Assert.NotEqual(a, b);

        // 20 de octeți în Base32 = 32 de caractere, alfabet A–Z și 2–7.
        Assert.Equal(32, a.Length);
        Assert.All(a, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
    }

    [Fact]
    public void VerifyCode_CodulCurent_EsteAcceptat()
    {
        var service = Service();
        var secret  = service.GenerateSecret();

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var code = service.ComputeCode(Base32Decode(secret), step);

        Assert.True(service.VerifyCode(secret, code));
    }

    [Fact]
    public void VerifyCode_ToleraSpatiiCopiateDinAplicatie()
    {
        var service = Service();
        var secret  = service.GenerateSecret();

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var code = service.ComputeCode(Base32Decode(secret), step);

        // Utilizatorii copiază codul cu spațiu la mijloc din aplicația de telefon.
        // Un refuz aici ar fi un bug raportat ca „2FA nu merge”.
        Assert.True(service.VerifyCode(secret, $"{code[..3]} {code[3..]}"));
    }

    [Fact]
    public void VerifyCode_CodDinAfaraFerestrei_EsteRespins()
    {
        var service = Service(window: 1);
        var secret  = service.GenerateSecret();

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        // Cinci intervale în urmă = două minute și jumătate. Fereastra e ±1.
        var oldCode = service.ComputeCode(Base32Decode(secret), step - 5);

        Assert.False(service.VerifyCode(secret, oldCode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]      // prea scurt
    [InlineData("1234567")]    // prea lung
    [InlineData("abcdef")]     // fără cifre
    public void VerifyCode_IntrareInvalida_ReturneazaFalse(string? code)
    {
        var service = Service();
        Assert.False(service.VerifyCode(service.GenerateSecret(), code));
    }

    [Fact]
    public void VerifyCode_SecretMalformat_ReturneazaFalse_FaraExceptie()
    {
        // Un secret corupt în baza de date nu trebuie să producă 500 la login.
        Assert.False(Service().VerifyCode("nu-este-base32!!!", "123456"));
    }

    // ── Coduri de recuperare ─────────────────────────────────────────────────

    [Fact]
    public void GenerateRecoveryCodes_FaraCaractereAmbigue()
    {
        var codes = Service().GenerateRecoveryCodes();

        Assert.Equal(10, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());

        foreach (var code in codes)
        {
            Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}$", code);

            // Utilizatorul le scrie pe hârtie și le tastează sub stres, exact
            // atunci când și-a pierdut telefonul. 0/O și 1/I/L sunt scoase din
            // alfabet tocmai ca să nu apară acolo.
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('1', code);
        }
    }

    [Fact]
    public void HashRecoveryCode_EsteDeterministSiDiferitDeCodulInClar()
    {
        var code = "ABCD-2345";

        var h1 = TotpService.HashRecoveryCode(code);
        var h2 = TotpService.HashRecoveryCode(code);

        Assert.Equal(h1, h2);
        Assert.NotEqual(code, h1);
        Assert.NotEqual(TotpService.HashRecoveryCode("ABCD-2346"), h1);
    }

    [Fact]
    public void BuildOtpAuthUri_ContineIssuerSiParametrii()
    {
        var uri = Service().BuildOtpAuthUri("ion.popescu", "JBSWY3DPEHPK3PXP");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);

        // Aplicațiile de telefon ignoră în practică parametrul algorithm și
        // presupun SHA1; îl declarăm explicit ca să nu rămână ambiguu.
        Assert.Contains("algorithm=SHA1", uri);
    }

    // ── Ajutor local: decodare Base32, ca să nu expunem metoda privată ────────
    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bits   = 0;
        var value  = 0;
        var output = new List<byte>();

        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) throw new FormatException($"Caracter Base32 invalid: {c}");

            value = (value << 5) | index;
            bits += 5;

            if (bits < 8) continue;

            output.Add((byte)(value >> (bits - 8)));
            bits -= 8;
        }

        return output.ToArray();
    }
}
