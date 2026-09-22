using System.IO;
using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Host-ul aplicatiei reale, cu baza de date PostgreSQL din Testcontainers.
///
/// De ce Testcontainers si nu InMemory: provider-ul in memorie al EF Core nu
/// suporta tranzactii, ILike, indexuri case-insensitive sau tipuri PostgreSQL.
/// Un test care trece pe InMemory si pica pe Npgsql nu valoreaza nimic.
///
/// Containerul se creeaza o singura data per clasa de test (IClassFixture) si
/// se opreste automat la sfarsit. Baza e curata: migrarile se aplica pe un
/// container proaspat, iar fiecare clasa de test isi populeaza datele singura.
/// </summary>
public sealed class SgdmWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Program.cs validates configuration before WebApplicationFactory.ConfigureWebHost()
    // gets a chance to apply UseSetting()/ConfigureServices(). Therefore the values
    // required only to let the real host build must be present in the environment
    // before Program starts. ConfigureWebHost() below still replaces the actual
    // database and storage services with the test implementations.
    static SgdmWebFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        // Valid, non-production test values. The database value only has to pass
        // Program.cs validation; the actual Testcontainers connection string is
        // injected later in ConfigureServices().
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            "Host=127.0.0.1;Port=5432;Database=sgdm_test;Username=test;Password=test");
        Environment.SetEnvironmentVariable(
            "MAI_JWT_KEY",
            "integration-test-jwt-key-0123456789-abcdefghijklmnopqrstuvwxyz");

        // Prevent startup validation from requiring MinIO/master encryption keys.
        // The registered storage is replaced with InMemoryFileStorage below.
        Environment.SetEnvironmentVariable("Storage__Provider", "Local");
        Environment.SetEnvironmentVariable("StorageEncryption__Enabled", "false");
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("sgdm_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    /// <summary>Conexiunea reala la baza din container, pentru seed sau verificari directe.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Creeaza un scope DI si returneaza un AppDbContext proaspat.</summary>
    public AppDbContext CreateDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    // ── IAsyncLifetime ──────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Aplicam migrarile EF Core pe baza din container, exact ca in productie.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // ── Configurare WebApplicationFactory ────────────────────────────────────

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Mediul Development permite secrete sablon si pepper-ul de test.
        builder.UseEnvironment("Development");

        // Configurarea de baza trebuie sa fie coerenta INAINTE ca Program.cs sa
        // construiasca serviciile (Validate() pe optiuni).
        builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting("Storage:LocalRootPath", Path.Combine(Path.GetTempPath(), $"sgdm-test-{Guid.NewGuid():N}"));

        // Rate limiting: dezactivat in teste. In productie limitele sunt mici
        // (10 login-uri / 5 min, 5 scrieri de parola / 15 min), dar testele
        // fac zeci de login-uri in cateva secunde, toate de pe 127.0.0.1.
        builder.UseSetting("RateLimit:LoginPermitLimit", "100000");
        builder.UseSetting("RateLimit:LoginWindowMinutes", "1");
        builder.UseSetting("RateLimit:RefreshPermitLimit", "100000");
        builder.UseSetting("RateLimit:RefreshWindowMinutes", "1");
        builder.UseSetting("RateLimit:PasswordWritePermitLimit", "100000");
        builder.UseSetting("RateLimit:PasswordWriteWindowMinutes", "1");

        builder.ConfigureServices(services =>
        {
            // Inlocuim inregistrarea PostgreSQL: chiar daca UseSetting a pus
            // connection string-ul corect, AddDbContext din Program.cs l-a
            // inregistrat deja cu valoarea de acolo. Reinlocuim explicit ca
            // sa foloseasca conexiunea din container.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));

            // Stocarea: un depozit in memorie, fara MinIO.
            services.RemoveAll<IFileStorage>();
            services.RemoveAll<MAI.BusinessLogic.Storage.S3FileStorage>();
            services.RemoveAll<MAI.BusinessLogic.Storage.LocalFileStorage>();
            services.AddSingleton<IFileStorage, InMemoryFileStorage>();

            // Email: un noop care inregistreaza apelurile, fara SMTP.
            services.RemoveAll<IEmailService>();
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<FakeEmailService>());
        });
    }
}
