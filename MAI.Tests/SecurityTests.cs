using System.Security.Cryptography;
using MAI.Api.Services;
using MAI.BusinessLogic.Security;
using MAI.Domain.Entities;
using Xunit;

namespace MAI.Tests;

/// <summary>Cifrarea secretelor TOTP înainte de a ajunge în baza de date.</summary>
public class SecretProtectorTests
{
    private const string Key = "dGVzdC1jaGVpZS1kZS0zMi1vY3RldGktcGVudHJ1LXRlc3Rl";

    private static SecretProtector Protector(string? key = null) =>
        new(new TwoFactorOptions { EncryptionKey = key ?? Key });

    [Fact]
    public void ProtectApoiUnprotect_RecupereazaValoareaOriginala()
    {
        var protector = Protector();
        const string secret = "JBSWY3DPEHPK3PXP";

        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
    }

    [Fact]
    public void Protect_AceeasiIntrare_ProduceCifrotexteDiferite()
    {
        var protector = Protector();

        // Nonce aleatoriu per operație. Dacă ar fi egale, un atacator cu acces la
        // coloană ar vedea care utilizatori împart același secret — și, mai grav,
        // refolosirea nonce-ului în GCM sparge complet confidențialitatea.
        Assert.NotEqual(protector.Protect("acelasi"), protector.Protect("acelasi"));
    }

    [Fact]
    public void Unprotect_CifrotextAlterat_Arunca()
    {
        var protector = Protector();
        var cipher    = protector.Protect("JBSWY3DPEHPK3PXP");

        // Modificăm un octet din mijloc. AES-GCM e cifrare autentificată: trebuie
        // să eșueze zgomotos, nu să întoarcă un secret greșit cu care nicio
        // autentificare nu ar mai merge și nimeni nu ar ști de ce.
        var bytes = Convert.FromBase64String(cipher);
        bytes[bytes.Length / 2] ^= 0xFF;

        Assert.Throws<CryptographicException>(
            () => protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Unprotect_CuAltaCheie_Arunca()
    {
        var cipher = Protector().Protect("JBSWY3DPEHPK3PXP");

        var alta = Protector("YWx0YS1jaGVpZS1jb21wbGV0LWRpZmVyaXRhLTMyLW9jdA==");

        Assert.Throws<CryptographicException>(() => alta.Unprotect(cipher));
    }

    [Fact]
    public void Constructor_FaraCheie_Arunca()
    {
        // Mai bine nu pornește serverul decât să pornească fără să poată cifra
        // secretele 2FA.
        Assert.Throws<InvalidOperationException>(
            () => new SecretProtector(new TwoFactorOptions { EncryptionKey = "" }));
    }
}

/// <summary>Politica de parole aplicată la creare de cont și la schimbare.</summary>
public class PasswordPolicyTests
{
    private static PasswordPolicy Policy() => new(new PasswordPolicyOptions());

    [Theory]
    [InlineData("Parola-Sigura-2026!")]
    [InlineData("Zx9#mQr2LpTw")]
    public void Validate_ParolaPuternica_EsteAcceptata(string password)
    {
        Assert.True(Policy().Validate(password, "ion.popescu").IsValid);
    }

    [Theory]
    [InlineData("Ab1!xY", "prea scurtă")]
    [InlineData("parola-lunga-fara-nimic", "fără majusculă, cifră sau simbol")]
    [InlineData("PAROLA-LUNGA-FARA-NIMIC", "fără minusculă")]
    [InlineData("Password123!", "parolă banală")]
    [InlineData("", "goală")]
    public void Validate_ParolaSlaba_EsteRespinsa(string password, string motiv)
    {
        var result = Policy().Validate(password, "ion.popescu");

        Assert.False(result.IsValid, $"Ar fi trebuit respinsă ({motiv}): {password}");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_ParolaCareContineNumeleUtilizatorului_EsteRespinsa()
    {
        // Cea mai frecventă parolă „puternică” din instituții: numele contului plus
        // un an și un semn de exclamare.
        var result = Policy().Validate("Ion.Popescu-2026!", "ion.popescu");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_MesajulDeEroareNuContineParola()
    {
        // Mesajul ajunge în răspunsul HTTP și, de acolo, potențial în loguri.
        var result = Policy().Validate("scurt", "ion.popescu");

        Assert.DoesNotContain("scurt", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Politica de blocare progresivă a contului.</summary>
public class AccountLockoutServiceTests
{
    private static readonly LockoutOptions Options = new()
    {
        MaxFailedAttempts    = 3,
        BaseLockoutMinutes   = 5,
        MaxLockoutMinutes    = 60,
        AttemptWindowMinutes = 30,
    };

    private static AccountLockoutService Service() => new(Options);

    private static User NewUser() => new()
    {
        Id       = Guid.NewGuid(),
        Username = "ion.popescu",
    };

    [Fact]
    public void SubPrag_NuBlocheazaContul()
    {
        var service = Service();
        var user    = NewUser();

        for (var i = 1; i < Options.MaxFailedAttempts; i++)
        {
            var outcome = service.RegisterFailedAttempt(user);
            Assert.False(outcome.LockedOut);
            Assert.Equal(i, outcome.AttemptCount);
        }

        Assert.Null(user.LockoutEndsAt);
    }

    [Fact]
    public void LaPrag_BlocheazaContulPeDurataDeBaza()
    {
        var service = Service();
        var user    = NewUser();

        LockoutOutcome outcome = default;
        for (var i = 0; i < Options.MaxFailedAttempts; i++)
            outcome = service.RegisterFailedAttempt(user);

        Assert.True(outcome.LockedOut);
        Assert.Equal(Options.BaseLockoutMinutes, outcome.LockoutMinutes);
        Assert.NotNull(user.LockoutEndsAt);
        Assert.True(user.IsLockedOut);
    }

    [Fact]
    public void PestePrag_DurataCrestExponentialDarRamanePlafonata()
    {
        var service = Service();
        var user    = NewUser();

        LockoutOutcome outcome = default;
        for (var i = 0; i < 30; i++)
            outcome = service.RegisterFailedAttempt(user);

        // Fără plafon, 2^25 minute ar însemna blocare pe 50 de ani — adică un
        // atacator ar putea distruge permanent conturi greșind parola intenționat.
        Assert.True(outcome.LockedOut);
        Assert.Equal(Options.MaxLockoutMinutes, outcome.LockoutMinutes);
    }

    [Fact]
    public void EsecVechi_ResetaeazaContorul()
    {
        var service = Service();
        var user    = NewUser();

        user.FailedLoginAttempts = Options.MaxFailedAttempts - 1;
        user.LastFailedLoginAt   = DateTime.UtcNow.AddMinutes(-(Options.AttemptWindowMinutes + 1));

        var outcome = service.RegisterFailedAttempt(user);

        // Fereastră glisantă: o greșeală de acum o oră nu trebuie să contribuie la
        // blocarea de acum.
        Assert.Equal(1, outcome.AttemptCount);
        Assert.False(outcome.LockedOut);
    }

    [Fact]
    public void ResetCounters_DeblocheazaSiSterreContorul()
    {
        var service = Service();
        var user    = NewUser();

        for (var i = 0; i < Options.MaxFailedAttempts; i++)
            service.RegisterFailedAttempt(user);

        service.ResetCounters(user);

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndsAt);
        Assert.False(user.IsLockedOut);
    }

    [Fact]
    public void RemainingLockoutSeconds_ZeroCandContulNuEBlocat()
    {
        Assert.Equal(0, Service().RemainingLockoutSeconds(NewUser()));
    }

    [Fact]
    public void AuditDetails_ContineNumerelePotrivite()
    {
        var outcome = new LockoutOutcome(2, 5, false, 0);
        Assert.Contains("(2/5)", outcome.AuditDetails);

        var locked = new LockoutOutcome(5, 5, true, 10);
        Assert.Contains("10 minute", locked.AuditDetails);
    }
}

/// <summary>
/// Validarea configurării JWT. Testele de aici apără exact clasa de greșeli care
/// nu se vede la rulare: serverul pornește, totul pare să meargă, iar tokenurile
/// sunt falsificabile.
/// </summary>
public class JwtOptionsTests
{
    private static JwtOptions Valid() => new()
    {
        Key                = "cheie-de-test-suficient-de-lunga-pentru-hs256-48",
        Issuer             = "SGDM",
        Audience           = "sgdm-frontend",
        AccessTokenMinutes = 15,
        RefreshTokenDays   = 7,
    };

    [Fact]
    public void Validate_ConfigurareCoreecta_NuArunca()
    {
        Valid().Validate();
    }

    [Fact]
    public void Validate_CheieSablon_Arunca()
    {
        var options = Valid();
        options.Key = JwtOptions.PlaceholderKey;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("prea-scurta")]
    [InlineData("31-de-caractere-exact-aici-1234")]
    public void Validate_CheiePreaScurta_Arunca(string key)
    {
        var options = Valid();
        options.Key = key;

        // HS256 cu o cheie scurtă se poate sparge offline pornind de la un singur
        // token interceptat.
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_IssuerSauAudienceGol_Arunca()
    {
        var faraIssuer = Valid();
        faraIssuer.Issuer = "";
        Assert.Throws<InvalidOperationException>(faraIssuer.Validate);

        var faraAudience = Valid();
        faraAudience.Audience = "";
        Assert.Throws<InvalidOperationException>(faraAudience.Validate);
    }

    [Fact]
    public void Validate_TokenDeAccesCuViataPreaLunga_Arunca()
    {
        var options = Valid();
        options.AccessTokenMinutes = 24 * 60;

        // Un token de acces valabil o zi anulează revocarea la logout: rămâne bun
        // ore întregi după ce sesiunea a fost încheiată.
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
