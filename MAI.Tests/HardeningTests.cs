using System.Security.Claims;
using MAI.Api.Security;
using MAI.BusinessLogic.Security;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Valorile-șablon din fișierele versionate trebuie recunoscute, iar secretele
/// reale nu trebuie confundate cu ele. Ambele direcții contează: un fals negativ
/// lasă să pornească un server cu cheie publică, un fals pozitiv oprește un
/// server configurat corect.
/// </summary>
public class PlaceholderSecretsTests
{
    [Theory]
    [InlineData("GENERATI_CU_openssl_rand_base64_48")]      // .env.example, MAI_JWT_KEY
    [InlineData("GENERATI_CU_openssl_rand_base64_32")]      // .env.example, pepper și cheia 2FA
    [InlineData("YOUR_JWT_SECRET_KEY_HERE")]                // appsettings.json
    [InlineData("YOUR_ARGON2_PEPPER_HERE")]                 // appsettings.json
    [InlineData("SCHIMBA_MA_MINIM_8_CARACTERE")]            // .env.example, MinIO
    [InlineData("Host=localhost;Database=sgdm;Username=sgdm;Password=SCHIMBA_MA")]
    public void IsPlaceholder_ValoareDinSabloane_EsteRecunoscuta(string value)
    {
        Assert.True(PlaceholderSecrets.IsPlaceholder(value));
    }

    [Theory]
    [InlineData("q8JxN0m3w9V4YtZ2kP7sR1uH6cE5aB0dF3gL8nQ2vX4=")]              // openssl rand -base64 32
    [InlineData("u5hT2qW9zK1mN8bV3cX6yL0pR4sD7fG2jH5kA9eQ1wZ3xC6vB8nM2lP0oI4uY7tR")]
    [InlineData("Host=db.intern;Database=sgdm;Username=sgdm_app;Password=Tq7!mZ2#pL9x")]
    public void IsPlaceholder_SecretReal_NuEsteConfundat(string value)
    {
        Assert.False(PlaceholderSecrets.IsPlaceholder(value));
    }

    [Fact]
    public void IsPlaceholder_ValoareLipsa_NuEsteSablon()
    {
        // Lipsa unui secret e tratată separat, cu mesaj propriu („lipsește”),
        // nu ca „valoare-șablon”.
        Assert.False(PlaceholderSecrets.IsPlaceholder(null));
        Assert.False(PlaceholderSecrets.IsPlaceholder(""));
        Assert.False(PlaceholderSecrets.IsPlaceholder("   "));
    }
}

/// <summary>
/// Cheia JWT din .env.example are 34 de octeți, deci trecea de verificarea de
/// lungime. Cine copia șablonul fără să-l completeze rula cu o cheie publică.
/// </summary>
public class JwtOptionsTemplateKeyTests
{
    private static JwtOptions WithKey(string key) => new()
    {
        Key                = key,
        Issuer             = "SGDM",
        Audience           = "sgdm-frontend",
        AccessTokenMinutes = 15,
        RefreshTokenDays   = 7,
    };

    [Fact]
    public void Validate_CheiaDinEnvExample_Arunca()
    {
        var options = WithKey("GENERATI_CU_openssl_rand_base64_48");

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_CheieSablonInDevelopment_EsteTolerata()
    {
        // Program.cs trece allowTemplateKey = true doar în Development, unde
        // pornirea continuă cu un avertisment în log.
        var options = WithKey("GENERATI_CU_openssl_rand_base64_48");

        options.Validate(allowTemplateKey: true);
    }

    [Fact]
    public void Validate_PlaceholderulVechi_AruncaInOriceMediu()
    {
        // „YOUR_JWT_SECRET_KEY_HERE” are și sub 32 de octeți: nu e acceptat nici
        // în Development.
        var options = WithKey(JwtOptions.PlaceholderKey);

        Assert.Throws<InvalidOperationException>(() => options.Validate(allowTemplateKey: true));
    }
}

/// <summary>
/// Decizia filtrului care aplică TwoFactor:RequiredForPrivilegedRoles.
/// Testată fără server: filtrul expune decizia ca metodă statică.
/// </summary>
public class PrivilegedMfaFilterTests
{
    private const string MappedAmr = "http://schemas.microsoft.com/claims/authnmethodsreferences";

    private static ClaimsPrincipal Authenticated(string amrType, string amrValue) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin.test"),
                new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(amrType, amrValue),
            },
            authenticationType: "Test"));

    private static readonly object[] AdminOnly =
        { new AuthorizeAttribute { Roles = "Administrator" } };

    [Fact]
    public void OptiuneaDezactivata_NuCereNimic()
    {
        Assert.False(PrivilegedMfaFilter.RequiresSecondFactor(
            enabled: false, Authenticated("amr", "pwd"), AdminOnly));
    }

    [Fact]
    public void EndpointPrivilegiat_TokenFaraMfa_EsteBlocat()
    {
        Assert.True(PrivilegedMfaFilter.RequiresSecondFactor(
            enabled: true, Authenticated("amr", "pwd"), AdminOnly));
    }

    [Theory]
    [InlineData("amr")]
    [InlineData(MappedAmr)]   // forma produsă de JwtBearer cu MapInboundClaims activ
    public void EndpointPrivilegiat_TokenCuMfa_EstePermis(string amrType)
    {
        Assert.False(PrivilegedMfaFilter.RequiresSecondFactor(
            enabled: true, Authenticated(amrType, "mfa"), AdminOnly));
    }

    [Fact]
    public void EndpointFaraRol_NuCereMfa()
    {
        // Transferurile, profilul și înrolarea 2FA rămân accesibile: altfel un
        // cont privilegiat nu și-ar mai putea activa al doilea factor.
        var anyAuthenticated = new object[] { new AuthorizeAttribute() };

        Assert.False(PrivilegedMfaFilter.RequiresSecondFactor(
            enabled: true, Authenticated("amr", "pwd"), anyAuthenticated));
    }

    [Fact]
    public void EndpointAnonim_NuCereMfa()
    {
        var anonymous = new object[] { new AllowAnonymousAttribute() };

        Assert.False(PrivilegedMfaFilter.RequiresSecondFactor(
            enabled: true, Authenticated("amr", "pwd"), anonymous));
    }

    [Fact]
    public void UtilizatorNeautentificat_NuEsteTratatDeFiltru()
    {
        // Autentificarea lipsă e treaba middleware-ului de autorizare (401),
        // nu a acestui filtru.
        var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(PrivilegedMfaFilter.RequiresSecondFactor(
            enabled: true, anonymousUser, AdminOnly));
    }
}
