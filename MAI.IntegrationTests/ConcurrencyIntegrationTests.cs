using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MAI.Api.Middleware;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Cererile paralele pe PostgreSQL real: tokenurile xmin, indexul unic pe
/// versiuni, refolosirea refresh token-ului și limita absolută a sesiunii.
///
/// Testele cu cereri paralele nu presupun o anumită ordine: serverul poate
/// executa cererile una după alta sau suprapus, în funcție de planificare.
/// Asertările sunt invariante care trebuie să țină în ORICE ordine - de
/// exemplu „exact un răspuns 200” sau „fiecare versiune are obiectul ei” -
/// deci testele nu sunt instabile, iar un eșec înseamnă o cursă reală.
/// </summary>
[Collection("Integration")]
public class ConcurrencyIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private const int Parallel = 6;

    private readonly SgdmWebFactory _factory;

    public ConcurrencyIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
    }

    // ── Token xmin, direct pe bază ────────────────────────────────────────────

    [Fact]
    public async Task DouaSalvariPeAcelasiUtilizator_ADouaEsteRespinsa()
    {
        // Scenariul din spatele tuturor celorlalte: două cereri citesc același
        // rând, fiecare își crește contorul, amândouă salvează. Fără token,
        // a doua suprascria prima și o incrementare se pierdea.
        var user = await TestHelpers.SeedUserAsync(_factory, "conc.xmin");

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

        var a = await dbA.Users.SingleAsync(u => u.Id == user.Id);
        var b = await dbB.Users.SingleAsync(u => u.Id == user.Id);

        a.FailedLoginAttempts++;
        b.FailedLoginAttempts++;

        await dbA.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        using var check = _factory.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(1, saved.FailedLoginAttempts);
    }

    // ── Versiuni de document încărcate simultan ───────────────────────────────

    [Fact]
    public async Task VersiuniSimultane_FiecareVersiuneAreObiectulEi_SiNumereUnice()
    {
        await TestHelpers.SeedUserAsync(_factory, "conc.doc.chief", role: UserRole.SefDirectie);
        var client = _factory.CreateClient();
        await TestHelpers.LoginAsync(client, "conc.doc.chief");

        var created = await client.PostAsync("/api/Documents", DocumentForm(
            ("Title", "Ordin test concurenta"), ("Category", "Ordin"), ("Number", "1/2026")));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var docId = (await TestHelpers.ReadJsonAsync(created)).GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i =>
            client.PostAsync($"/api/Documents/{docId}/versions",
                DocumentForm(("ChangeNotes", $"redactia {i}")))));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.All(statuses, s => Assert.True(
            s is HttpStatusCode.OK or HttpStatusCode.Conflict, $"Cod neasteptat: {s}"));

        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var body = await TestHelpers.ReadJsonAsync(conflict);
            Assert.Equal(ConcurrencyConflictMiddleware.ConflictCode, body.GetProperty("code").GetString());
        }

        var accepted = statuses.Count(s => s == HttpStatusCode.OK);
        Assert.True(accepted >= 1, "Cel puțin o versiune trebuie să treacă.");

        using var scope = _factory.Services.CreateScope();
        var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = (InMemoryFileStorage)scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var doc      = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
        var versions = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == docId)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();

        // Versiunea inițială + câte au primit 200, fără goluri și fără dubluri.
        Assert.Equal(1 + accepted, versions.Count);
        Assert.Equal(Enumerable.Range(1, versions.Count), versions.Select(v => v.VersionNumber));
        Assert.Equal(versions.Count, doc.CurrentVersion);

        // Fiecare versiune are propriul obiect, iar conținutul lui e exact cel
        // pentru care s-a calculat amprenta. Înainte, două versiuni ajungeau pe
        // aceeași cheie și amprenta uneia nu mai corespundea conținutului.
        Assert.Equal(versions.Count, versions.Select(v => v.EncryptedStoragePath).Distinct().Count());

        foreach (var version in versions)
        {
            Assert.True(storage.Contains(version.EncryptedStoragePath),
                $"Lipsește obiectul versiunii {version.VersionNumber}: {version.EncryptedStoragePath}");

            var actual = Convert.ToHexString(SHA256.HashData(storage.Read(version.EncryptedStoragePath))).ToLowerInvariant();
            Assert.Equal(version.ChecksumSHA256, actual);
        }

        // Cererile respinse și-au retras obiectele: în depozit rămân doar cele
        // referite din bază.
        Assert.Equal(versions.Count, storage.KeysWithPrefix($"documents/{docId:N}/").Count);

        // Fiecare 409 lasă un rând de audit.
        var conflicts = statuses.Count(s => s == HttpStatusCode.Conflict);
        var audited   = await db.AuditLogs.CountAsync(a =>
            a.Action == AuditAction.ConcurrencyConflict && a.Details.Contains(docId.ToString()));
        Assert.Equal(conflicts, audited);
    }

    // ── Refresh token refolosit ───────────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenRotit_PrezentatDinNou_InchideSesiunea()
    {
        var user   = await TestHelpers.SeedUserAsync(_factory, "conc.reuse");
        var client = _factory.CreateClient();
        var login  = await TestHelpers.LoginAsync(client, "conc.reuse");

        // Rotația normală: T1 → T2.
        var rotated = await client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = await TestHelpers.ReadJsonAsync(rotated);
        var t2     = second.GetProperty("refreshToken").GetString()!;
        var a2     = second.GetProperty("accessToken").GetString()!;

        // T1 din nou: a doua parte care îl avea. Sesiunea se închide.
        var reuse = await client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // Închisă pentru amândouă: nici T2, nici tokenul de acces emis cu el.
        var afterReuse = await client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken = t2 });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", a2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Transfers")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.UserSessions.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.NotNull(session.RevokedAt);
        Assert.Equal("refolosire refresh token", session.RevokedReason);

        Assert.True(await db.AuditLogs.AnyAsync(a =>
            a.UserId == user.Id && a.Action == AuditAction.RefreshTokenReused && a.Result == AuditResult.Failure));
    }

    [Fact]
    public async Task AcelasiRefreshToken_InCereriSimultane_OSinguraRotatie_SiSesiuneInchisa()
    {
        var user   = await TestHelpers.SeedUserAsync(_factory, "conc.refresh.burst");
        var client = _factory.CreateClient();
        var login  = await TestHelpers.LoginAsync(client, "conc.refresh.burst");

        var responses = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ =>
            client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken = login.RefreshToken })));

        // Oricum ar fi fost programate cererile: prima care ajunge în bază
        // rotește (200); oricare alta vede fie tokenul anterior, fie conflictul
        // pe xmin, și închide sesiunea. Deci exact un 200 și sesiunea închisă.
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

        var winner = await TestHelpers.ReadJsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
        var again  = await client.PostAsJsonAsync("/api/Auth/refresh",
            new { refreshToken = winner.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.UserSessions.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.NotNull(session.RevokedAt);
    }

    // ── 2FA: același cod trimis în rafală ─────────────────────────────────────

    [Fact]
    public async Task CodTotpCorect_TrimisInParalel_DeschideOSinguraSesiune()
    {
        var user   = await TestHelpers.SeedUserAsync(_factory, "conc.totp");
        var secret = await EnableTwoFactorAsync(user.Id);

        var client = _factory.CreateClient();
        var login  = await client.PostAsJsonAsync("/api/Auth/login", new { username = "conc.totp", password = "TestParola1!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var challenge = (await TestHelpers.ReadJsonAsync(login)).GetProperty("challengeToken").GetString()!;
        var code      = CurrentCode(secret);

        var responses = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ =>
            client.PostAsJsonAsync("/api/Auth/2fa/verify", new { challengeToken = challenge, code })));

        // O provocare, un cod: o singură sesiune, oricâte cereri ar sosi. Restul
        // găsesc provocarea consumată (401) sau pierd cursa pe xmin (409).
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(
            r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict, $"Cod neasteptat: {r.StatusCode}"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.UserSessions.CountAsync(s => s.UserId == user.Id));
    }

    // ── Durata absolută a sesiunii ────────────────────────────────────────────

    [Fact]
    public async Task Sesiunea_NuPoateFiPrelungitaPesteLimitaAbsoluta()
    {
        var user   = await TestHelpers.SeedUserAsync(_factory, "conc.absolute");
        var client = _factory.CreateClient();
        var login  = await TestHelpers.LoginAsync(client, "conc.absolute");

        var hours = _factory.Services.GetRequiredService<JwtOptions>().SessionAbsoluteHours;

        using (var scope = _factory.Services.CreateScope())
        {
            var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.UserSessions.AsNoTracking().SingleAsync(s => s.UserId == user.Id);

            Assert.InRange(session.AbsoluteExpiresAt,
                session.CreatedAt.AddHours(hours).AddSeconds(-5), session.CreatedAt.AddHours(hours).AddSeconds(5));
            Assert.True(session.ExpiresAt <= session.AbsoluteExpiresAt);
            Assert.True(login.RefreshTokenExpiresAt <= session.AbsoluteExpiresAt.AddSeconds(1));
        }

        // Sesiunea se apropie de sfârșit: rotația nu mai poate adăuga 7 zile.
        var limit = DateTime.UtcNow.AddMinutes(5);
        await SetSessionTimesAsync(user.Id, absolute: limit, expires: limit);

        var rotated = await client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await TestHelpers.ReadJsonAsync(rotated);

        var reported = body.GetProperty("refreshTokenExpiresAt").GetDateTime().ToUniversalTime();
        Assert.InRange(reported, limit.AddSeconds(-1), limit.AddSeconds(1));

        // Limita a trecut: refresh-ul e refuzat, oricât de recent a fost folosit.
        var past = DateTime.UtcNow.AddMinutes(-1);
        await SetSessionTimesAsync(user.Id, absolute: past, expires: past);

        var expired = await client.PostAsJsonAsync("/api/Auth/refresh",
            new { refreshToken = body.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    // ── Utilitare ─────────────────────────────────────────────────────────────

    private static MultipartFormDataContent DocumentForm(params (string Name, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
            content.Add(new StringContent(value), name);

        // Conținut diferit la fiecare cerere: dacă două versiuni ar ajunge în
        // același obiect, amprenta uneia n-ar mai corespunde.
        content.Add(new ByteArrayContent(RandomNumberGenerator.GetBytes(2048)), "File", "ordin.pdf");
        return content;
    }

    private async Task<string> EnableTwoFactorAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db        = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var totp      = scope.ServiceProvider.GetRequiredService<TotpService>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();

        var secret = totp.GenerateSecret();
        var user   = await db.Users.SingleAsync(u => u.Id == userId);

        user.TwoFactorEnabled    = true;
        user.TwoFactorSecret     = protector.Protect(secret);
        user.TwoFactorEnrolledAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return secret;
    }

    private string CurrentCode(string secret)
    {
        var totp    = _factory.Services.GetRequiredService<TotpService>();
        var options = _factory.Services.GetRequiredService<TwoFactorOptions>();
        var step    = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / options.PeriodSeconds;

        return totp.ComputeCode(TotpService.Base32Decode(secret), step);
    }

    private async Task SetSessionTimesAsync(Guid userId, DateTime absolute, DateTime expires)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.UserSessions
            .Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.AbsoluteExpiresAt, absolute)
                .SetProperty(s => s.ExpiresAt, expires));
    }
}
