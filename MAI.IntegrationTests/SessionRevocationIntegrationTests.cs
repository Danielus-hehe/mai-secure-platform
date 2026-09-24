using System.Net;
using System.Net.Http.Json;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Tokenul de acces e legat de sesiunea din care provine (claim-ul „sid”).
///
/// Înainte, închiderea unei sesiuni revoca doar refresh token-ul: tokenul de
/// acces deja emis mergea în continuare până la 15 minute. Testele de aici
/// folosesc exact ACELAȘI token înainte și după revocare.
///
/// Plus schimbarea parolei unui cont cu chei E2EE: parola și pachetul
/// reîmpachetat se salvează împreună, iar fără pachet schimbarea e refuzată.
/// </summary>
[Collection("Integration")]
public class SessionRevocationIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private readonly SgdmWebFactory _factory;

    public SessionRevocationIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Delogarea_InvalideazaImediatTokenulDeAcces()
    {
        await TestHelpers.SeedUserAsync(_factory, "rev.logout");

        var client = _factory.CreateClient();
        var login  = await TestHelpers.LoginAsync(client, "rev.logout");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Transfers")).StatusCode);

        var logout = await client.PostAsJsonAsync("/api/Auth/logout", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // Același token de acces, încă neexpirat: sesiunea lui e închisă.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Transfers")).StatusCode);
    }

    [Fact]
    public async Task DezactivareaContului_InvalideazaImediatTokenulDeAcces()
    {
        await TestHelpers.SeedUserAsync(_factory, "rev.admin", role: UserRole.Administrator);
        var target = await TestHelpers.SeedUserAsync(_factory, "rev.target");

        var targetClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(targetClient, "rev.target");
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/Transfers")).StatusCode);

        var adminClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(adminClient, "rev.admin");
        var deactivate = await adminClient.PatchAsync($"/api/Users/{target.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/Transfers")).StatusCode);
    }

    [Fact]
    public async Task SchimbareaParolei_CuChei_CerePachetulReimpachetat()
    {
        var user = await TestHelpers.SeedUserAsync(_factory, "rev.keys", withKeys: true);

        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "rev.keys");

        // Fără pachet: refuzat, iar parola rămâne cea veche.
        var refused = await client.PatchAsJsonAsync("/api/Auth/change-password", new
        {
            currentPassword = "TestParola1!",
            newPassword     = "AltaParola#2026Xq",
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await TestHelpers.ReadJsonAsync(refused);
        Assert.Equal("KEYS_REWRAP_REQUIRED", body.GetProperty("code").GetString());

        // Cu pachet: parola și cheile se schimbă în aceeași operație.
        var bundle = TestHelpers.FakeBase64(2048);
        var salt   = TestHelpers.FakeBase64(16);
        var iv     = TestHelpers.FakeBase64(12);

        var accepted = await client.PatchAsJsonAsync("/api/Auth/change-password", new
        {
            currentPassword = "TestParola1!",
            newPassword     = "AltaParola#2026Xq",
            keys = new
            {
                encryptedPrivateBundle  = bundle,
                keyDerivationSalt       = salt,
                keyDerivationIterations = 600_000,
                wrapIv                  = iv,
            },
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        await using var db = _factory.CreateDbContext();
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        Assert.Equal(bundle, stored.EncryptedPrivateBundle);
        Assert.Equal(salt, stored.KeyDerivationSalt);
        Assert.Equal(iv, stored.KeyWrapIv);

        // Schimbarea parolei închide toate sesiunile, inclusiv pe cea curentă.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Transfers")).StatusCode);

        var relogin = _factory.CreateClient();
        await TestHelpers.LoginAsync(relogin, "rev.keys", "AltaParola#2026Xq");
    }
}
