using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.BusinessLogic.Services;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Teste pe hashingul de parole.
///
/// Profilele folosite aici sunt deliberat mai ieftine decât cele din producție
/// (8 MiB, o iterație): un test care rulează Argon2id la 19 MiB de zeci de ori
/// devine un test pe care nimeni nu-l mai rulează. Ce se verifică - formatul PHC,
/// unicitatea sării, migrarea, rezultatul verificării - nu depinde de cost.
/// </summary>
public class Argon2PasswordHasherTests
{
    private const string CorrectPassword = "Parola-Corecta-2026!";

    private static Argon2Options CheapOptions(bool allowLegacy = false) => new()
    {
        Profiles = new Dictionary<string, Argon2Profile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Interactive"] = new() { MemorySizeKib = 8192, Iterations = 1, DegreeOfParallelism = 1 },
            ["Sensitive"]   = new() { MemorySizeKib = 8192, Iterations = 2, DegreeOfParallelism = 1 },
        },
        DefaultProfile       = "Interactive",
        PrivilegedProfile    = "Sensitive",
        MaxConcurrentHashes  = 2,
        QueueTimeoutSeconds  = 10,
        AllowLegacyPlaintext = allowLegacy,
    };

    private static Argon2PasswordHasher Hasher(Argon2Options? options = null) =>
        new(options ?? CheapOptions());

    [Fact]
    public async Task Hash_ProduceFormatPhcArgon2id()
    {
        var hash = await Hasher().HashPasswordAsync(CorrectPassword);

        // Formatul PHC nu e cosmetic: parametrii de cost sunt codificați ÎN hash,
        // deci verificarea unei parole vechi folosește costul cu care a fost creată,
        // nu pe cel din configurația de azi. Fără asta, orice ajustare de cost ar
        // invalida toate parolele existente.
        Assert.StartsWith("$argon2id$v=19$", hash);

        // 5 segmente: argon2id | v=19 | parametri | sare | hash
        Assert.Equal(5, hash.Split('$', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Hash_AceeasiParola_ProduceHashuriDiferite()
    {
        var hasher = Hasher();

        var a = await hasher.HashPasswordAsync(CorrectPassword);
        var b = await hasher.HashPasswordAsync(CorrectPassword);

        // Sare aleatorie per hash. Dacă ar fi egale, un dump al bazei ar arăta
        // instantaneu care utilizatori au aceeași parolă.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Verify_ParolaCorecta_ReturneazaSuccess()
    {
        var hasher = Hasher();
        var hash   = await hasher.HashPasswordAsync(CorrectPassword);

        Assert.Equal(PasswordVerificationResult.Success,
            await hasher.VerifyPasswordAsync(CorrectPassword, hash));
    }

    [Theory]
    [InlineData("Parola-Gresita-2026!")]
    [InlineData("parola-corecta-2026!")]   // aceeași parolă, altă capitalizare
    [InlineData("Parola-Corecta-2026")]    // lipsește ultimul caracter
    [InlineData("")]
    public async Task Verify_ParolaGresita_ReturneazaFailed(string wrong)
    {
        var hasher = Hasher();
        var hash   = await hasher.HashPasswordAsync(CorrectPassword);

        Assert.Equal(PasswordVerificationResult.Failed,
            await hasher.VerifyPasswordAsync(wrong, hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nu-este-un-hash-phc")]
    [InlineData("$argon2id$v=19$trunchiat")]
    public async Task Verify_HashInvalid_ReturneazaFailed_FaraExceptie(string? storedHash)
    {
        // Un hash corupt în baza de date trebuie să însemne „nu te pot autentifica”,
        // nu o excepție nemanipulată care se scurge ca 500 și confirmă atacatorului
        // că a nimerit un cont real.
        Assert.Equal(PasswordVerificationResult.Failed,
            await Hasher(CheapOptions(allowLegacy: true)).VerifyPasswordAsync("orice", storedHash));
    }

    [Fact]
    public async Task Verify_ParolaInClar_CerreRehash_CandMigrareaEPermisa()
    {
        var hasher = Hasher(CheapOptions(allowLegacy: true));

        // Starea inițială a bazei: parole stocate în clar. Trebuie acceptate O DATĂ,
        // ca să poată fi migrate transparent la primul login.
        var result = await hasher.VerifyPasswordAsync(CorrectPassword, CorrectPassword);

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public async Task Verify_ParolaInClar_Respinsa_CandMigrareaENinterzisa()
    {
        var hasher = Hasher(CheapOptions(allowLegacy: false));

        // După încheierea migrării, o parolă rămasă în clar nu mai e o cale de
        // autentificare - e un rând care trebuie reparat de administrator.
        Assert.Equal(PasswordVerificationResult.Failed,
            await hasher.VerifyPasswordAsync(CorrectPassword, CorrectPassword));
    }

    [Fact]
    public async Task Simulate_NuAruncaSiConsumaTimp()
    {
        // Apelată când utilizatorul nu există. Dacă ar returna instantaneu, latența
        // răspunsului ar spune atacatorului care nume de utilizator sunt reale.
        var hasher = Hasher();

        var start = DateTime.UtcNow;
        await hasher.SimulateVerificationAsync();
        var elapsed = DateTime.UtcNow - start;

        Assert.True(elapsed > TimeSpan.Zero,
            "SimulateVerificationAsync trebuie să consume timp de calcul real.");
    }

    [Fact]
    public async Task Hash_ProfilPrivilegiat_CostMaiMareDecatCelImplicit()
    {
        var options = CheapOptions();
        var hasher  = Hasher(options);

        var normal      = await hasher.HashPasswordAsync(CorrectPassword, options.DefaultProfile);
        var privilegiat = await hasher.HashPasswordAsync(CorrectPassword, options.PrivilegedProfile);

        // Parametrii sunt vizibili în string-ul PHC; conturile privilegiate trebuie
        // să plătească explicit mai mult.
        Assert.Contains("t=1", normal);
        Assert.Contains("t=2", privilegiat);
    }

    [Fact]
    public void Options_ProfilInexistent_CadeInapoiPeCelImplicit()
    {
        var options = CheapOptions();

        // O greșeală de scriere în appsettings nu trebuie să arunce la runtime,
        // în mijlocul unui login.
        Assert.Same(options.Profiles["Interactive"], options.GetProfile("ProfilCareNuExista"));
    }
}
