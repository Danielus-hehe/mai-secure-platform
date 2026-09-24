using System.Net;
using MAI.DataAccessLayer;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Datele panourilor de securitate din administrare: acoperirea 2FA și
/// gruparea alertelor pe tip.
/// </summary>
[Collection("Integration")]
public class SecurityPanelsIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private readonly SgdmWebFactory _factory;

    public SecurityPanelsIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AcoperireaTwoFactor_ImparteConturileActive_SiNuExpuneSecrete()
    {
        var admin    = await TestHelpers.SeedUserAsync(_factory, "sp.admin", role: UserRole.Administrator);
        var withTotp = await TestHelpers.SeedUserAsync(_factory, "sp.cu2fa");
        await TestHelpers.SeedUserAsync(_factory, "sp.fara2fa", role: UserRole.SefDirectie);
        var inactive = await TestHelpers.SeedUserAsync(_factory, "sp.inactiv");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == withTotp.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TwoFactorEnabled, true)
                .SetProperty(u => u.TwoFactorSecret, "secret-cifrat-de-test")
                .SetProperty(u => u.TwoFactorEnrolledAt, DateTime.UtcNow));
            await db.Users.Where(u => u.Id == inactive.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false));
        }

        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "sp.admin");

        var response = await client.GetAsync("/api/Stats/two-factor");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw  = await response.Content.ReadAsStringAsync();
        var body = await TestHelpers.ReadJsonAsync(response);

        var enabled  = body.GetProperty("enabled").EnumerateArray().Select(p => p.GetProperty("username").GetString()).ToList();
        var disabled = body.GetProperty("disabled").EnumerateArray().Select(p => p.GetProperty("username").GetString()).ToList();

        Assert.Contains("sp.cu2fa", enabled);
        Assert.Contains("sp.fara2fa", disabled);
        Assert.Contains("sp.admin", disabled);

        // Un cont dezactivat nu se poate autentifica: nu contează pentru acoperire.
        Assert.DoesNotContain("sp.inactiv", enabled.Concat(disabled));

        Assert.Equal(enabled.Count, body.GetProperty("enabledCount").GetInt32());
        Assert.Equal(disabled.Count, body.GetProperty("disabledCount").GetInt32());
        Assert.True(body.GetProperty("privilegedWithoutCount").GetInt32() >= 2);

        // Doar starea, niciodată secretul TOTP.
        Assert.DoesNotContain("secret-cifrat-de-test", raw);
        Assert.NotEqual(Guid.Empty, admin.Id);
    }

    [Fact]
    public async Task AcoperireaTwoFactor_UtilizatorObisnuit_EsteRefuzat()
    {
        await TestHelpers.SeedUserAsync(_factory, "sp.obisnuit");
        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "sp.obisnuit");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/Stats/two-factor")).StatusCode);
    }

    [Fact]
    public async Task Alertele_AuCheieDeGrup_PentruSectiunilePliabile()
    {
        // Un cont privilegiat fără 2FA produce mereu cel puțin o alertă.
        await TestHelpers.SeedUserAsync(_factory, "sp.alerte.admin", role: UserRole.Administrator);
        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "sp.alerte.admin");

        var body   = await TestHelpers.ReadJsonAsync(await client.GetAsync("/api/Stats/alerts"));
        var alerts = body.GetProperty("alerts").EnumerateArray().ToList();

        Assert.NotEmpty(alerts);
        Assert.All(alerts, a => Assert.False(string.IsNullOrEmpty(a.GetProperty("group").GetString())));
        Assert.Contains(alerts, a => a.GetProperty("group").GetString() == "privileged-no-2fa");
    }
}
