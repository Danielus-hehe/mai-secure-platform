using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MAI.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Teste de integrare pentru ciclul complet al transferurilor.
///
/// Fiecare test isi creeaza utilizatorii sai (cu prefix unic), asa ca nu
/// exista dependenta de ordinea de rulare. Baza PostgreSQL e reala,
/// migrarile sunt aplicate, iar depozitul e in memorie.
///
/// Scenariile acoperite:
///   1. Transfer cu mai multi destinatari + dovada de primire per destinatar
///   2. Forward permis si interzis (AllowForward) + audit
///   3. Retragere (revoke) - sterge cifrotextul, blocheaza descarcarea
///   4. Stergere logica - nu se poate sterge un transfer activ
///   5. Anti-IDOR: un utilizator nu poate citi sau confirma transferul altcuiva
/// </summary>
[Collection("Integration")]
public class TransferIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private readonly SgdmWebFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TransferIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    // ═════════════════════════════════════════════════════════════════════
    // 1. Transfer multi-destinatar cu dovada de primire per destinatar
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MultiRecipient_EachConfirmsSeparately_StatusBecomesDownloadedOnlyWhenAllConfirm()
    {
        // Arrange: 1 expeditor, 2 destinatari
        var sender = await TestHelpers.SeedUserAsync(_factory, "mr.sender");
        var recv1  = await TestHelpers.SeedUserAsync(_factory, "mr.recv1");
        var recv2  = await TestHelpers.SeedUserAsync(_factory, "mr.recv2");

        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "mr.sender");

        // Act: trimite un fisier catre amandoi
        var upload = await TestHelpers.BuildUploadContent(
            recv1.Id,
            extraRecipients: [(recv2.Id, TestHelpers.FakeBase64(384))]);

        var uploadResp = await client.PostAsync("/api/Transfers", upload);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadJson = await TestHelpers.ReadJsonAsync(uploadResp);
        var transferId = uploadJson.GetProperty("id").GetString();
        Assert.Equal(2, uploadJson.GetProperty("recipients").GetInt32());

        // Primul destinatar confirma - starea ramane Pending
        var client1 = _factory.CreateClient();
        await TestHelpers.LoginAsync(client1, "mr.recv1");

        var confirm1 = await client1.PatchAsJsonAsync(
            $"/api/Transfers/{transferId}/confirm",
            new { signatureValid = true });
        Assert.Equal(HttpStatusCode.OK, confirm1.StatusCode);

        // Verificam starea din lista expeditorului
        var listResp = await client.GetAsync($"/api/Transfers?direction=sent");
        var list     = await TestHelpers.ReadJsonAsync(listResp);
        var items    = list.GetProperty("items");
        var transfer = FindTransfer(items, transferId!);
        Assert.Equal("Pending", transfer.GetProperty("status").GetString());
        Assert.Equal(1, transfer.GetProperty("downloadedCount").GetInt32());

        // Al doilea destinatar confirma - starea devine Downloaded
        var client2 = _factory.CreateClient();
        await TestHelpers.LoginAsync(client2, "mr.recv2");

        var confirm2 = await client2.PatchAsJsonAsync(
            $"/api/Transfers/{transferId}/confirm",
            new { signatureValid = true });
        Assert.Equal(HttpStatusCode.OK, confirm2.StatusCode);

        // Reverificam
        listResp = await client.GetAsync($"/api/Transfers?direction=sent");
        list     = await TestHelpers.ReadJsonAsync(listResp);
        items    = list.GetProperty("items");
        transfer = FindTransfer(items, transferId!);
        Assert.Equal("Downloaded", transfer.GetProperty("status").GetString());
        Assert.Equal(2, transfer.GetProperty("downloadedCount").GetInt32());
    }

    [Fact]
    public async Task MultiRecipient_DoubleConfirm_IsIdempotent()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "dc.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "dc.recv");

        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "dc.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await client.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        var recvClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(recvClient, "dc.recv");

        // Prima confirmare
        var c1 = await recvClient.PatchAsJsonAsync($"/api/Transfers/{id}/confirm", new { signatureValid = true });
        Assert.Equal(HttpStatusCode.OK, c1.StatusCode);

        // A doua confirmare - trebuie sa fie OK (idempotenta)
        var c2 = await recvClient.PatchAsJsonAsync($"/api/Transfers/{id}/confirm", new { signatureValid = false });
        Assert.Equal(HttpStatusCode.OK, c2.StatusCode);
        var json = await TestHelpers.ReadJsonAsync(c2);
        Assert.True(json.GetProperty("alreadyConfirmed").GetBoolean());
    }

    // ═════════════════════════════════════════════════════════════════════
    // 2. Forward: AllowForward + audit
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Forward_WhenAllowed_AddsRecipientAndCreatesAuditEntry()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "fwd.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "fwd.recv");
        var fwdTo  = await TestHelpers.SeedUserAsync(_factory, "fwd.target");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "fwd.sender");

        // Transfer cu AllowForward = true
        var upload = await TestHelpers.BuildUploadContent(recv.Id, allowForward: true);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Destinatarul face forward
        var recvClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(recvClient, "fwd.recv");

        var fwdResp = await recvClient.PostAsJsonAsync($"/api/Transfers/{id}/forward", new
        {
            recipients = new[] { new { userId = fwdTo.Id, encryptedKeyForUser = TestHelpers.FakeBase64(384) } },
        });
        Assert.Equal(HttpStatusCode.OK, fwdResp.StatusCode);

        // Noul destinatar poate confirma
        var fwdClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(fwdClient, "fwd.target");

        var envelope = await fwdClient.GetAsync($"/api/Transfers/{id}/envelope");
        Assert.Equal(HttpStatusCode.OK, envelope.StatusCode);

        var confirm = await fwdClient.PatchAsJsonAsync(
            $"/api/Transfers/{id}/confirm", new { signatureValid = true });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // Verificam intrarea de audit (TransferForwarded)
        // Autentificat ca admin sau ca sender - vedem jurnalul
        var auditResp = await senderClient.GetAsync("/api/AuditLogs?pageSize=50");
        // Expeditorul poate sa nu aiba acces la audit daca nu e admin,
        // dar in acest test ne intereseaza ca forward-ul a reusit.
        // Verificam in baza de date direct.
        using var db = _factory.CreateDbContext();
        var auditEntry = db.AuditLogs
            .Where(a => a.Action == AuditAction.TransferForwarded)
            .OrderByDescending(a => a.Timestamp)
            .FirstOrDefault();
        Assert.NotNull(auditEntry);
        Assert.Contains("fwd target", auditEntry.Details);
    }

    [Fact]
    public async Task Forward_WhenNotAllowed_Returns403()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "nfwd.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "nfwd.recv");
        var target = await TestHelpers.SeedUserAsync(_factory, "nfwd.target");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "nfwd.sender");

        // AllowForward = false (implicit)
        var upload = await TestHelpers.BuildUploadContent(recv.Id, allowForward: false);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Destinatarul incearca forward
        var recvClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(recvClient, "nfwd.recv");

        var fwdResp = await recvClient.PostAsJsonAsync($"/api/Transfers/{id}/forward", new
        {
            recipients = new[] { new { userId = target.Id, encryptedKeyForUser = TestHelpers.FakeBase64(384) } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, fwdResp.StatusCode);
    }

    [Fact]
    public async Task Forward_SenderCanAlwaysForward_EvenWhenAllowForwardIsFalse()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "sfwd.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "sfwd.recv");
        var target = await TestHelpers.SeedUserAsync(_factory, "sfwd.target");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "sfwd.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id, allowForward: false);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Expeditorul face forward - trebuie sa mearga intotdeauna
        var fwdResp = await senderClient.PostAsJsonAsync($"/api/Transfers/{id}/forward", new
        {
            recipients = new[] { new { userId = target.Id, encryptedKeyForUser = TestHelpers.FakeBase64(384) } },
        });
        Assert.Equal(HttpStatusCode.OK, fwdResp.StatusCode);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 3. Retragere (revoke)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Revoke_DeletesCiphertext_BlocksDownload()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "rev.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "rev.recv");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "rev.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Retrage
        var revokeResp = await senderClient.PostAsJsonAsync(
            $"/api/Transfers/{id}/revoke",
            new { reason = "document gresit" });
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // Destinatarul nu mai poate descarca
        var recvClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(recvClient, "rev.recv");

        var envelopeResp = await recvClient.GetAsync($"/api/Transfers/{id}/envelope");
        Assert.Equal(HttpStatusCode.Gone, envelopeResp.StatusCode);

        var contentResp = await recvClient.GetAsync($"/api/Transfers/{id}/content");
        Assert.Equal(HttpStatusCode.Gone, contentResp.StatusCode);

        // Confirmarea dupa retragere e refuzata
        var confirmResp = await recvClient.PatchAsJsonAsync(
            $"/api/Transfers/{id}/confirm", new { signatureValid = true });
        Assert.Equal(HttpStatusCode.Gone, confirmResp.StatusCode);
    }

    [Fact]
    public async Task Revoke_OnlyBySender_RecipientGetsForbid()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "revs.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "revs.recv");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "revs.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Destinatarul incearca sa retraga - interzis
        var recvClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(recvClient, "revs.recv");

        var revokeResp = await recvClient.PostAsJsonAsync(
            $"/api/Transfers/{id}/revoke", new { reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, revokeResp.StatusCode);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 4. Stergere logica
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SoftDelete_ActiveTransfer_IsRejected()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "sdel.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "sdel.recv");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "sdel.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Transfer activ (Pending) nu se poate sterge direct
        var delResp = await senderClient.DeleteAsync($"/api/Transfers/{id}");
        Assert.Equal(HttpStatusCode.Conflict, delResp.StatusCode);
    }

    [Fact]
    public async Task SoftDelete_AfterRevoke_Succeeds_TransferDisappearsFromList()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "sdelr.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "sdelr.recv");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "sdelr.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Retrage mai intai
        await senderClient.PostAsJsonAsync($"/api/Transfers/{id}/revoke", new { reason = "retras" });

        // Acum stergerea logica trebuie sa mearga
        var delResp = await senderClient.DeleteAsync($"/api/Transfers/{id}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        // Transferul nu mai apare in lista
        var listResp = await senderClient.GetAsync("/api/Transfers");
        var list     = await TestHelpers.ReadJsonAsync(listResp);
        var items    = list.GetProperty("items");
        Assert.Null(TryFindTransfer(items, id!));
    }

    [Fact]
    public async Task SoftDelete_AfterAllConfirm_Succeeds()
    {
        var sender = await TestHelpers.SeedUserAsync(_factory, "sdelc.sender");
        var recv   = await TestHelpers.SeedUserAsync(_factory, "sdelc.recv");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "sdelc.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Destinatarul confirma
        var recvClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(recvClient, "sdelc.recv");
        await recvClient.PatchAsJsonAsync($"/api/Transfers/{id}/confirm", new { signatureValid = true });

        // Transferul e Downloaded - stergerea logica merge
        var delResp = await senderClient.DeleteAsync($"/api/Transfers/{id}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 5. Anti-IDOR
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Idor_StrangerCannotReadEnvelope()
    {
        var sender  = await TestHelpers.SeedUserAsync(_factory, "idor.sender");
        var recv    = await TestHelpers.SeedUserAsync(_factory, "idor.recv");
        var stranger = await TestHelpers.SeedUserAsync(_factory, "idor.stranger");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "idor.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        // Strainul incearca sa citeasca plicul
        var strangerClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(strangerClient, "idor.stranger");

        var envelopeResp = await strangerClient.GetAsync($"/api/Transfers/{id}/envelope");
        Assert.Equal(HttpStatusCode.Forbidden, envelopeResp.StatusCode);
    }

    [Fact]
    public async Task Idor_StrangerCannotDownloadContent()
    {
        var sender  = await TestHelpers.SeedUserAsync(_factory, "idordl.sender");
        var recv    = await TestHelpers.SeedUserAsync(_factory, "idordl.recv");
        var stranger = await TestHelpers.SeedUserAsync(_factory, "idordl.stranger");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "idordl.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        var strangerClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(strangerClient, "idordl.stranger");

        var contentResp = await strangerClient.GetAsync($"/api/Transfers/{id}/content");
        Assert.Equal(HttpStatusCode.Forbidden, contentResp.StatusCode);
    }

    [Fact]
    public async Task Idor_StrangerCannotConfirmReceipt()
    {
        var sender  = await TestHelpers.SeedUserAsync(_factory, "idorcf.sender");
        var recv    = await TestHelpers.SeedUserAsync(_factory, "idorcf.recv");
        var stranger = await TestHelpers.SeedUserAsync(_factory, "idorcf.stranger");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "idorcf.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        var strangerClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(strangerClient, "idorcf.stranger");

        var confirmResp = await strangerClient.PatchAsJsonAsync(
            $"/api/Transfers/{id}/confirm", new { signatureValid = true });
        Assert.Equal(HttpStatusCode.Forbidden, confirmResp.StatusCode);
    }

    [Fact]
    public async Task Idor_StrangerCannotRevokeTransfer()
    {
        var sender  = await TestHelpers.SeedUserAsync(_factory, "idorrev.sender");
        var recv    = await TestHelpers.SeedUserAsync(_factory, "idorrev.recv");
        var stranger = await TestHelpers.SeedUserAsync(_factory, "idorrev.stranger");

        var senderClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(senderClient, "idorrev.sender");

        var upload = await TestHelpers.BuildUploadContent(recv.Id);
        var resp   = await senderClient.PostAsync("/api/Transfers", upload);
        resp.EnsureSuccessStatusCode();
        var id = (await TestHelpers.ReadJsonAsync(resp)).GetProperty("id").GetString();

        var strangerClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(strangerClient, "idorrev.stranger");

        var revokeResp = await strangerClient.PostAsJsonAsync(
            $"/api/Transfers/{id}/revoke", new { reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, revokeResp.StatusCode);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════

    private static JsonElement FindTransfer(JsonElement items, string id)
    {
        var found = TryFindTransfer(items, id);
        Assert.NotNull(found);
        return found.Value;
    }

    private static JsonElement? TryFindTransfer(JsonElement items, string id)
    {
        foreach (var item in items.EnumerateArray())
            if (item.GetProperty("id").GetString() == id)
                return item;
        return null;
    }
}
