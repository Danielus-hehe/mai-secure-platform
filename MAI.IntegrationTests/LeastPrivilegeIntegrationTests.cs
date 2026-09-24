using System.Net;
using MAI.DataAccessLayer;
using MAI.DataAccessLayer.Maintenance;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Privilegiile minime, pe PostgreSQL real:
///   - rolul aplicației creat de db:migrate chiar nu poate modifica jurnalul;
///   - un șef de direcție vede doar jurnalul subdiviziunii lui.
/// </summary>
[Collection("Integration")]
public class LeastPrivilegeIntegrationTests : IClassFixture<SgdmWebFactory>
{
    private const string AppRole     = "sgdm_app_it";
    private const string AppPassword = "ParolaRolAplicatieTest2026";

    private readonly SgdmWebFactory _factory;

    public LeastPrivilegeIntegrationTests(SgdmWebFactory factory)
    {
        _factory = factory;
    }

    // ── Rolul aplicației ──────────────────────────────────────────────────────

    private async Task<AppRoleReport> ProvisionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await DatabaseRoleProvisioner.EnsureAppRoleAsync(db, AppRole, AppPassword);
    }

    private NpgsqlConnection ConnectAsApp()
    {
        var builder = new NpgsqlConnectionStringBuilder(_factory.ConnectionString)
        {
            Username = AppRole,
            Password = AppPassword,
            Pooling  = false,
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static async Task<int> ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection connection, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(connection, sql));
        // 42501 = insufficient_privilege. Orice alt cod ar însemna că instrucțiunea
        // a eșuat din alt motiv, iar testul n-ar dovedi nimic despre drepturi.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task RolulAplicatiei_ScrieInJurnal_DarNuIlPoateModificaSauSterge()
    {
        var report = await ProvisionAsync();
        Assert.True(report.IsLeastPrivilege, $"Drepturi neasteptate: {report}");

        // A doua rulare (orice „docker compose up”) nu schimbă nimic și nu eșuează.
        var again = await ProvisionAsync();
        Assert.False(again.Created);
        Assert.True(again.IsLeastPrivilege);

        await using var app = ConnectAsApp();
        await app.OpenAsync();

        // Ce folosește aplicația: citire, scriere în tabele, adăugare în jurnal.
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM \"Users\"", app))
            Assert.True((long)(await read.ExecuteScalarAsync())! >= 0);

        Assert.Equal(1, await ExecAsync(app,
            "INSERT INTO \"AuditLogs\" (\"Id\", \"Username\", \"Action\", \"Details\", \"Result\", \"IpAddress\", \"Timestamp\") " +
            "VALUES (gen_random_uuid(), 'rol.aplicatie', 0, 'test privilegii', 0, '127.0.0.1', now())"));

        // Ce nu are voie nici un API compromis.
        await AssertDeniedAsync(app, "UPDATE \"AuditLogs\" SET \"Details\" = 'modificat'");
        await AssertDeniedAsync(app, "DELETE FROM \"AuditLogs\"");
        await AssertDeniedAsync(app, "TRUNCATE \"AuditLogs\"");
        await AssertDeniedAsync(app, "DELETE FROM \"__EFMigrationsHistory\"");
        await AssertDeniedAsync(app, "CREATE TABLE ddl_interzis (id int)");
        await AssertDeniedAsync(app, "ALTER TABLE \"Users\" ADD COLUMN interzis int");
    }

    [Fact]
    public async Task RlsRamasDePeSupabase_FaraPolitici_EsteDezactivat_CuPoliticiEsteRaportat()
    {
        // Exact baza mutată de pe Supabase: RLS activat pe tabele, politicile
        // sărite la restaurare. Proprietarul trece peste RLS, rolul aplicației nu:
        // fără reparație, primul INSERT în AuditLogs (la login) dădea 500.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" ENABLE ROW LEVEL SECURITY");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE rls_cu_politica (id int); " +
                "ALTER TABLE rls_cu_politica ENABLE ROW LEVEL SECURITY; " +
                "CREATE POLICY p_test ON rls_cu_politica FOR SELECT USING (true);");
        }

        try
        {
            var report = await ProvisionAsync();

            Assert.Contains("AuditLogs", report.RowSecurityDisabled);
            Assert.Contains("rls_cu_politica", report.RowSecurityWithPolicies);
            Assert.DoesNotContain("rls_cu_politica", report.RowSecurityDisabled);

            // Rolul aplicației poate din nou adăuga în jurnal.
            await using var app = ConnectAsApp();
            await app.OpenAsync();
            Assert.Equal(1, await ExecAsync(app,
                "INSERT INTO \"AuditLogs\" (\"Id\", \"Username\", \"Action\", \"Details\", \"Result\", \"IpAddress\", \"Timestamp\") " +
                "VALUES (gen_random_uuid(), 'rol.aplicatie', 0, 'dupa RLS', 0, '127.0.0.1', now())"));

            // A doua rulare nu mai are ce dezactiva.
            Assert.Empty((await ProvisionAsync()).RowSecurityDisabled);
        }
        finally
        {
            // Tabelul cu politică ar face ca db:migrate să raporteze eroare în
            // celelalte teste care rulează pe aceeași bază.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS rls_cu_politica");
        }
    }

    [Fact]
    public async Task RolulAplicatiei_NuPoateFiProprietarul()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var owner = new NpgsqlConnectionStringBuilder(_factory.ConnectionString).Username!;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseRoleProvisioner.EnsureAppRoleAsync(db, owner, AppPassword));
    }

    // ── Jurnalul văzut de un șef de direcție ─────────────────────────────────

    [Fact]
    public async Task SefDeDirectie_VedeDoarJurnalulSubdiviziuniiSale()
    {
        var chief    = await TestHelpers.SeedUserAsync(_factory, "lp.sef", role: UserRole.SefDirectie);
        var outsider = await TestHelpers.SeedUserAsync(_factory, "lp.strain");

        var marker = $"lp-{Guid.NewGuid():N}";

        Guid memberId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var directorate = new OrgUnit { Name = $"Directia {marker}", Type = OrgUnitType.Directie, HeadUserId = chief.Id };
            var section     = new OrgUnit { Name = $"Sectia {marker}", Type = OrgUnitType.Sectie, ParentId = directorate.Id };
            var other       = new OrgUnit { Name = $"Alta {marker}", Type = OrgUnitType.Directie };
            db.OrgUnits.AddRange(directorate, section, other);
            await db.SaveChangesAsync();

            await db.Users.Where(u => u.Id == chief.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.OrgUnitId, directorate.Id));
            await db.Users.Where(u => u.Id == outsider.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.OrgUnitId, other.Id));

            memberId = (await TestHelpers.SeedUserAsync(_factory, "lp.membru", orgUnitId: section.Id)).Id;

            db.AuditLogs.AddRange(
                Entry(memberId,    "lp.membru", $"{marker} membru"),
                Entry(outsider.Id, "lp.strain", $"{marker} strain"),
                Entry(null,        "inexistent", $"{marker} anonim"));
            await db.SaveChangesAsync();
        }

        var chiefClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(chiefClient, "lp.sef");

        var seen = await DetailsAsync(chiefClient, marker);
        Assert.Contains($"{marker} membru", seen);
        Assert.DoesNotContain($"{marker} strain", seen);
        Assert.DoesNotContain($"{marker} anonim", seen);

        // Nici filtrul explicit pe un utilizator din altă direcție nu ocolește regula.
        var forced = await chiefClient.GetAsync($"/api/AuditLogs?username=lp.strain&search={marker}");
        Assert.Equal(0, (await TestHelpers.ReadJsonAsync(forced)).GetProperty("totalCount").GetInt32());

        // Lista de nume din filtru nu dezvăluie cine a folosit sistemul în alte direcții.
        var names = await TestHelpers.ReadJsonAsync(await chiefClient.GetAsync("/api/AuditLogs/usernames"));
        var nameList = names.EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains("lp.membru", nameList);
        Assert.DoesNotContain("lp.strain", nameList);

        // Administratorul vede tot.
        await TestHelpers.SeedUserAsync(_factory, "lp.admin", role: UserRole.Administrator);
        var adminClient = _factory.CreateClient();
        await TestHelpers.LoginAsync(adminClient, "lp.admin");

        var all = await DetailsAsync(adminClient, marker);
        Assert.Contains($"{marker} membru", all);
        Assert.Contains($"{marker} strain", all);
        Assert.Contains($"{marker} anonim", all);
    }

    private static AuditLog Entry(Guid? userId, string username, string details) => new()
    {
        UserId    = userId,
        Username  = username,
        Action    = AuditAction.FileUpload,
        Details   = details,
        Result    = AuditResult.Success,
        IpAddress = "10.0.0.1",
        Timestamp = DateTime.UtcNow,
    };

    private static async Task<List<string>> DetailsAsync(HttpClient client, string marker)
    {
        var response = await client.GetAsync($"/api/AuditLogs?search={marker}&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await TestHelpers.ReadJsonAsync(response);
        return body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("target").GetString()!)
            .ToList();
    }
}
