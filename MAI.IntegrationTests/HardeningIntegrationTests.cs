using System.Net;
using System.Net.Http.Json;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Regresiile din runda de întărire dinaintea prezentării, pe API-ul real și
/// PostgreSQL real (Testcontainers):
///   1. Parola temporară stabilită de administrator blochează API-ul pe server,
///      nu doar în interfață, până la schimbarea ei.
///   2. Un transfer legitim către mulți destinatari cu nume lungi nu mai eșuează
///      din cauza lungimii rândului de audit.
/// </summary>
[Collection("Integration")]
public class HardeningIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private readonly SgdmWebFactory _factory;

    public HardeningIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ParolaTemporara_BlocheazaApiul_PanaLaSchimbare()
    {
        await TestHelpers.SeedUserAsync(_factory, "hrd.admin", role: UserRole.Administrator);
        var user = await TestHelpers.SeedUserAsync(_factory, "hrd.temp");

        // Administratorul stabilește o parolă temporară.
        var adminClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(adminClient, "hrd.admin");

        const string temporary = "Provizorie#2026Xq";
        var reset = await adminClient.PostAsJsonAsync($"/api/Users/{user.Id}/reset-password",
            new { newPassword = temporary });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // Cu parola temporară, login-ul reușește, dar API-ul e închis...
        var client = _factory.CreateClient();
        var login  = await TestHelpers.LoginAsync(client, "hrd.temp", temporary);
        Assert.True(login.MustChangePassword);

        var transfers = await client.GetAsync("/api/Transfers");
        Assert.Equal(HttpStatusCode.Forbidden, transfers.StatusCode);
        var body = await TestHelpers.ReadJsonAsync(transfers);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", body.GetProperty("code").GetString());

        var documents = await client.GetAsync("/api/InternalDocuments");
        Assert.Equal(HttpStatusCode.Forbidden, documents.StatusCode);

        // ...în afara ecranului de schimbare a parolei.
        var keys = await client.GetAsync("/api/Keys/me");
        Assert.Equal(HttpStatusCode.OK, keys.StatusCode);

        const string own = "ProprieNoua#2026Xq";
        var change = await client.PatchAsJsonAsync("/api/Auth/change-password",
            new { currentPassword = temporary, newPassword = own });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // După schimbare, tokenul nou nu mai poartă restricția.
        var fresh = _factory.CreateClient();
        var relogin = await TestHelpers.LoginAsync(fresh, "hrd.temp", own);
        Assert.False(relogin.MustChangePassword);

        var after = await fresh.GetAsync("/api/Transfers");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task TransferCatreDouazeciDeDestinatari_CuNumeLungi_Reuseste()
    {
        await TestHelpers.SeedUserAsync(_factory, "hrd.sender");

        // 20 de destinatari (maximul implicit), fiecare cu nume complet lung.
        // Împreună cu un nume de fișier de 250 de caractere, textul de audit
        // depășea cu mult 1024 de caractere și trimiterea dădea 500.
        var recipients = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            var name = $"hrd.destinatar.{i:D2}.cu.un.nume.foarte.lung.de.test";
            var r = await TestHelpers.SeedUserAsync(_factory, name);
            recipients.Add(r.Id);
        }

        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "hrd.sender");

        var fileName = new string('r', 242) + ".pdf.enc";
        var content  = await TestHelpers.BuildUploadContent(
            recipients[0],
            fileName: fileName,
            extraRecipients: recipients.Skip(1).Select(id => (id, TestHelpers.FakeBase64(384))).ToList());

        var response = await client.PostAsync("/api/Transfers", content);
        var text     = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");

        await using var db = _factory.CreateDbContext();
        var audit = await db.AuditLogs
            .Where(a => a.Username == "hrd.sender" && a.Action == AuditAction.FileUpload)
            .OrderByDescending(a => a.Timestamp)
            .FirstAsync();

        Assert.True(audit.Details.Length <= 1024);
        Assert.EndsWith(MAI.DataAccessLayer.AuditLogLimits.TruncationMarker, audit.Details);
    }
}
