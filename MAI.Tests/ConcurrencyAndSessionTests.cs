using System.Reflection;
using MAI.Api.Configuration;
using MAI.Api.Middleware;
using MAI.Api.Services;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Maparea tokenurilor de concurență și a indexului unic pe versiuni.
///
/// Fără bază de date: modelul EF se construiește din configurări, deci aici se
/// verifică ce va trimite EF în SQL (WHERE xmin = ..., indexul unic). Faptul că
/// PostgreSQL respinge efectiv scrierile concurente e verificat în
/// MAI.IntegrationTests.ConcurrencyIntegrationTests.
/// </summary>
public class ConcurrencyModelTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=sgdm_design;Username=x;Password=x")
            .Options);

    /// <summary>
    /// Modelul de proiectare, nu cel de rulare: cel de rulare (context.Model)
    /// poate renunța la adnotări folosite doar de migrări, precum numele și
    /// filtrul indexurilor. Același model îl folosește și testul de snapshot.
    /// </summary>
    private static IModel DesignModel(AppDbContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    [Theory]
    [InlineData(typeof(User))]
    [InlineData(typeof(UserSession))]
    [InlineData(typeof(Document))]
    public void Entitatea_AreTokenDeConcurenta_PeXmin(Type entity)
    {
        using var context = CreateContext();

        var property = DesignModel(context).FindEntityType(entity)!.FindProperty("Version");

        Assert.NotNull(property);
        Assert.True(property!.IsConcurrencyToken,
            $"{entity.Name}.Version trebuie să fie token de concurență, altfel UPDATE nu verifică xmin.");
        Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
        Assert.Equal("xmin", property.GetColumnName());
        Assert.Equal("xid", property.GetColumnType());
    }

    [Fact]
    public void DocumentVersion_AreIndexUnic_PeDocumentSiNumar()
    {
        using var context = CreateContext();
        var entity = DesignModel(context).FindEntityType(typeof(DocumentVersion))!;

        var index = entity.GetIndexes().SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "DocumentId", "VersionNumber" }));

        Assert.NotNull(index);
        Assert.True(index!.IsUnique);
        Assert.Equal("UX_DocumentVersions_DocumentId_VersionNumber", index.GetDatabaseName());
    }

    [Fact]
    public void UserSession_AreIndexPeTokenulAnterior()
    {
        using var context = CreateContext();
        var entity = DesignModel(context).FindEntityType(typeof(UserSession))!;

        var index = entity.GetIndexes().SingleOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(UserSession.PreviousRefreshTokenHash));

        Assert.NotNull(index);
        Assert.False(index!.IsUnique);
        Assert.Equal("\"PreviousRefreshTokenHash\" IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void UltimaMigrare_ContineControlulDeConcurenta_SiDurataSesiunii()
    {
        var last = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Migration).IsAssignableFrom(t))
            .Select(t => (Type: t, Id: t.GetCustomAttribute<MigrationAttribute>()!.Id))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Last();

        Assert.Equal("20260924120000_ConcurrencyAndSessionLifetime", last.Id);

        var model   = ((Migration)Activator.CreateInstance(last.Type)!).TargetModel!;
        var session = model.FindEntityType("MAI.Domain.Entities.UserSession")!;

        Assert.NotNull(session.FindProperty("AbsoluteExpiresAt"));
        Assert.NotNull(session.FindProperty("PreviousRefreshTokenHash"));
        Assert.NotNull(session.FindProperty("Version"));
        Assert.NotNull(model.FindEntityType("MAI.Domain.Entities.User")!.FindProperty("Version"));
        Assert.NotNull(model.FindEntityType("MAI.Domain.Entities.Document")!.FindProperty("Version"));
    }
}

/// <summary>
/// Durata absolută a sesiunii: regula de plafonare și configurarea ei.
/// </summary>
public class SessionLifetimeTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cap_ExpirareInainteDeLimita_RamaneNeschimbata()
    {
        var requested = Now.AddHours(1);
        Assert.Equal(requested, SessionLifetime.Cap(requested, Now.AddHours(12)));
    }

    [Fact]
    public void Cap_ExpirareDupaLimita_EsteTaiataLaLimita()
    {
        // Refresh token de 7 zile într-o sesiune care se încheie peste o oră.
        var absolute = Now.AddHours(1);
        Assert.Equal(absolute, SessionLifetime.Cap(Now.AddDays(7), absolute));
    }

    [Fact]
    public void Cap_RotiriRepetate_NuMutaNiciodataLimita()
    {
        // Exact bug-ul reparat: fiecare rotație muta expirarea cu 7 zile.
        var absolute = Now.AddHours(12);
        var expires  = SessionLifetime.Cap(Now.AddDays(7), absolute);

        for (var i = 1; i <= 100; i++)
            expires = SessionLifetime.Cap(Now.AddMinutes(15 * i).AddDays(7), absolute);

        Assert.Equal(absolute, expires);
    }

    private static JwtOptions Valid() => new()
    {
        Key                = "cheie-de-test-suficient-de-lunga-pentru-hs256-48",
        Issuer             = "SGDM",
        Audience           = "sgdm-frontend",
        AccessTokenMinutes = 15,
        RefreshTokenDays   = 7,
    };

    [Fact]
    public void JwtOptions_ImplicitDouasprezeceOre()
    {
        var options = Valid();
        options.Validate();
        Assert.Equal(12, options.SessionAbsoluteHours);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void JwtOptions_ValoareNepozitiva_RevineLaImplicit(int hours)
    {
        var options = Valid();
        options.SessionAbsoluteHours = hours;
        options.Validate();
        Assert.Equal(12, options.SessionAbsoluteHours);
    }

    [Fact]
    public void JwtOptions_SesiuneMaiScurtaDecatTokenulDeAcces_Arunca()
    {
        var options = Valid();
        options.AccessTokenMinutes   = 90;
        options.SessionAbsoluteHours = 1;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void JwtOptions_PesteTreizeciDeZile_Arunca()
    {
        // Aproape sigur minute scrise în loc de ore.
        var options = Valid();
        options.SessionAbsoluteHours = 720 + 1;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void DotEnv_MapeazaDurataSesiunii_CaInDockerCompose()
    {
        Assert.True(DotEnvLoader.ComposeMapping.TryGetValue("SESSION_ABSOLUTE_HOURS", out var targets));
        Assert.Contains("Jwt__SessionAbsoluteHours", targets!);
    }
}

/// <summary>
/// Ce transformă middleware-ul în 409 și ce lasă să treacă.
/// </summary>
public class ConcurrencyConflictDescribeTests
{
    [Fact]
    public void Describe_ConflictDeConcurenta_EsteRecunoscut()
    {
        var ex = new DbUpdateConcurrencyException("xmin schimbat");
        Assert.NotNull(ConcurrencyConflictMiddleware.Describe(ex));
    }

    [Fact]
    public void Describe_IndexUnicIncalcat_EsteRecunoscut()
    {
        var pg = new PostgresException("duplicate key", "ERROR", "ERROR", "23505");
        var ex = new DbUpdateException("save failed", pg);

        var what = ConcurrencyConflictMiddleware.Describe(ex);

        Assert.NotNull(what);
        Assert.Contains("index unic", what);
    }

    [Fact]
    public void Describe_AltaEroareDeBaza_NuEsteConflict()
    {
        // 23503 = cheie străină încălcată: o eroare reală, nu o cursă. Trebuie
        // să rămână 500, ca să fie văzută și reparată.
        var pg = new PostgresException("fk", "ERROR", "ERROR", "23503");

        Assert.Null(ConcurrencyConflictMiddleware.Describe(new DbUpdateException("save failed", pg)));
        Assert.Null(ConcurrencyConflictMiddleware.Describe(new InvalidOperationException("altceva")));
    }
}
