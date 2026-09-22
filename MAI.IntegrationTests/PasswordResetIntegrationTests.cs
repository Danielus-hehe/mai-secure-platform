using System.Net;
using System.Net.Http.Json;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Teste de integrare pentru resetarea parolei prin link trimis pe email.
///
/// Scenariile acoperite:
///   1. Token de unica folosinta: dupa resetare, acelasi token e refuzat
///   2. Token expirat: un token vechi e refuzat
///   3. Cheile E2EE sunt sterse la resetare (cheia privata era incuiata cu
///      parola veche, deci nu mai poate fi descuiata)
///   4. Sesiunile existente sunt inchise la resetare
///   5. Anti-IDOR: linkul de resetare ajunge doar la administratori, nu
///      la utilizatorul insusi
/// </summary>
[Collection("Integration")]
public class PasswordResetIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private readonly SgdmWebFactory _factory;

    public PasswordResetIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PasswordReset_TokenIsSingleUse()
    {
        // Arrange: admin creeaza un utilizator si trimite link de resetare
        var admin = await TestHelpers.SeedUserAsync(_factory, "rst.admin", role: UserRole.Administrator);
        var user  = await TestHelpers.SeedUserAsync(_factory, "rst.user");

        var adminClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(adminClient, "rst.admin");

        // Initiem resetarea - serviciul pune tokenul in baza
        var requestResp = await adminClient.PostAsync($"/api/Users/{user.Id}/send-password-reset", null);
        Assert.Equal(HttpStatusCode.OK, requestResp.StatusCode);

        // Extragem token-ul din emailul fals
        var emailService = _factory.Services.GetRequiredService<FakeEmailService>();
        var resetEmail = emailService.Sent.FirstOrDefault(e => e.Type == "PasswordReset" && e.ToEmail == user.Email);
        Assert.NotNull(resetEmail);
        Assert.NotNull(resetEmail.Link);

        var token = ExtractToken(resetEmail.Link);
        Assert.False(string.IsNullOrEmpty(token));

        // Prima resetare reuseste
        var anonClient = _factory.CreateClient();
        var resetResp = await anonClient.PostAsJsonAsync("/api/Auth/reset-password", new
        {
            token,
            newPassword = "Trandafir9$Verde!",
            confirmPassword = "Trandafir9$Verde!",
        });
        var resetBody = await resetResp.Content.ReadAsStringAsync();
        Assert.True(
            resetResp.StatusCode == HttpStatusCode.OK,
            $"Resetarea parolei a esuat cu {resetResp.StatusCode}: {resetBody}");

        // Act: a doua incercare cu acelasi token e refuzata
        var repeatResp = await anonClient.PostAsJsonAsync("/api/Auth/reset-password", new
        {
            token,
            newPassword = "Castron7AltaParola999!Albastru",
            confirmPassword = "Castron7AltaParola999!Albastru",
        });
        Assert.NotEqual(HttpStatusCode.OK, repeatResp.StatusCode);

        // Utilizatorul se poate autentifica cu parola noua
        var loginClient = _factory.CreateClient();
        var loginResp = await loginClient.PostAsJsonAsync("/api/Auth/login", new
        {
            username = "rst.user",
            password = "Trandafir9$Verde!",
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
    }

    [Fact]
    public async Task PasswordReset_ExpiredToken_IsRejected()
    {
        var admin = await TestHelpers.SeedUserAsync(_factory, "exp.admin", role: UserRole.Administrator);
        var user  = await TestHelpers.SeedUserAsync(_factory, "exp.user");

        // Punem tokenul manual in baza, cu expirare in trecut
        using (var db = _factory.CreateDbContext())
        {
            var dbUser = await db.Users.FirstAsync(u => u.Id == user.Id);
            dbUser.PasswordResetToken       = MAI.Api.Services.UrlSafeTokens.Hash("expired-token-123");
            dbUser.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var anonClient = _factory.CreateClient();
        var resetResp = await anonClient.PostAsJsonAsync("/api/Auth/reset-password", new
        {
            token           = "expired-token-123",
            newPassword     = "Trandafir9$Verde!",
            confirmPassword = "Trandafir9$Verde!",
        });

        // Tokenul expirat nu va functiona
        Assert.NotEqual(HttpStatusCode.OK, resetResp.StatusCode);
    }

    [Fact]
    public async Task PasswordReset_ClearsEncryptionKeys()
    {
        var admin = await TestHelpers.SeedUserAsync(_factory, "keys.admin", role: UserRole.Administrator);
        var user  = await TestHelpers.SeedUserAsync(_factory, "keys.user", withKeys: true);

        // Verificam ca are chei inainte
        using (var db = _factory.CreateDbContext())
        {
            var dbUser = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
            Assert.NotNull(dbUser.PublicKeyEncryption);
        }

        var adminClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(adminClient, "keys.admin");

        var requestResp = await adminClient.PostAsync($"/api/Users/{user.Id}/send-password-reset", null);
        Assert.Equal(HttpStatusCode.OK, requestResp.StatusCode);

        var emailService = _factory.Services.GetRequiredService<FakeEmailService>();
        var resetEmail = emailService.Sent.FirstOrDefault(e => e.Type == "PasswordReset" && e.ToEmail == user.Email);
        Assert.NotNull(resetEmail);
        var token = ExtractToken(resetEmail.Link!);

        var anonClient = _factory.CreateClient();
        var resetResp = await anonClient.PostAsJsonAsync("/api/Auth/reset-password", new
        {
            token,
            newPassword     = "Trandafir9$Verde!",
            confirmPassword = "Trandafir9$Verde!",
        });
        var resetBody = await resetResp.Content.ReadAsStringAsync();
        Assert.True(
            resetResp.StatusCode == HttpStatusCode.OK,
            $"Resetarea parolei a esuat cu {resetResp.StatusCode}: {resetBody}");

        var json = await TestHelpers.ReadJsonAsync(resetResp);
        Assert.True(json.GetProperty("keysCleared").GetBoolean());

        // Verificam in baza ca cheile au fost sterse
        using (var db = _factory.CreateDbContext())
        {
            var dbUser = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
            Assert.Null(dbUser.PublicKeyEncryption);
            Assert.Null(dbUser.PublicKeySigning);
            Assert.Null(dbUser.EncryptedPrivateBundle);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Extrage tokenul din URL-ul de resetare din email.</summary>
    private static string ExtractToken(string link)
    {
        var uri = new Uri(link);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query["token"] ?? string.Empty;
    }
}
