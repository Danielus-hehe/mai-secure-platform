using MAI.Api.Configuration;
using MAI.Api.Tools;
using MAI.BusinessLogic.Organization;
using MAI.DataAccessLayer.Maintenance;
using MAI.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Regula de vizibilitate a jurnalului pentru șefii de direcție.
///
/// Structura de test:
///   Direcția D (șef: chief)
///     └─ Secția S (membri: a, fostul angajat c, dezactivat)
///   Direcția O (membru: b)
/// </summary>
public class AuditScopeTests
{
    private static readonly Guid Chief = Guid.NewGuid();
    private static readonly Guid A     = Guid.NewGuid();
    private static readonly Guid B     = Guid.NewGuid();
    private static readonly Guid C     = Guid.NewGuid();

    private static readonly Guid D = Guid.NewGuid();
    private static readonly Guid S = Guid.NewGuid();
    private static readonly Guid O = Guid.NewGuid();

    private static OrgTree Tree(bool directorateActive = true) => new(new[]
    {
        new OrgUnitNode(D, null, Chief, OrgUnitType.Directie, directorateActive, "Directia D"),
        new OrgUnitNode(S, D,    null,  OrgUnitType.Sectie,   true,              "Sectia S"),
        new OrgUnitNode(O, null, null,  OrgUnitType.Directie, true,              "Directia O"),
    });

    private static readonly OrgMember[] Members =
    {
        new(Chief, D, true),
        new(A,     S, true),
        new(C,     S, false),
        new(B,     O, true),
    };

    [Fact]
    public void Sef_VedeSubdiviziuneaSiSubunitatile_InclusivFostiiAngajati()
    {
        var visible = AuditScope.VisibleUserIds(Tree(), Members, Chief);

        Assert.Contains(Chief, visible);
        Assert.Contains(A, visible);
        // Un angajat dezactivat rămâne în istoricul subdiviziunii.
        Assert.Contains(C, visible);
    }

    [Fact]
    public void Sef_NuVedeAlteDirectii()
    {
        var visible = AuditScope.VisibleUserIds(Tree(), Members, Chief);
        Assert.DoesNotContain(B, visible);
    }

    [Fact]
    public void FaraSubdiviziuneCondusa_VedeDoarPropriileActiuni()
    {
        // „a” are rolul (ipotetic) de șef, dar nu conduce nicio unitate.
        var visible = AuditScope.VisibleUserIds(Tree(), Members, A);
        Assert.Equal(new[] { A }, visible);
    }

    [Fact]
    public void SubdiviziuneDezactivata_NuMaiDaAcces()
    {
        var visible = AuditScope.VisibleUserIds(Tree(directorateActive: false), Members, Chief);
        Assert.Equal(new[] { Chief }, visible);
    }
}

/// <summary>
/// Regulile comenzii db:migrate, verificate fără bază de date.
/// </summary>
public class DbMigrateCommandTests
{
    private const string Owner = "Host=postgres;Database=sgdm;Username=sgdm;Password=parolaProprietar123";

    private static DbMigrateCommand.Settings Settings(
        string? connection = Owner, string? user = "sgdm_app", string? password = "parolaAplicatiei1234567",
        bool require = true, bool list = false) =>
        new(connection, user, password, require, list);

    [Fact]
    public void Configuratie_Completa_EsteAcceptata()
    {
        Assert.Null(DbMigrateCommand.Validate(Settings(), isDevelopment: false));
    }

    [Fact]
    public void FaraConexiune_EsteRefuzata()
    {
        Assert.NotNull(DbMigrateCommand.Validate(Settings(connection: null), isDevelopment: false));
        Assert.NotNull(DbMigrateCommand.Validate(Settings(connection: "YOUR_CONNECTION_STRING"), isDevelopment: false));
    }

    [Fact]
    public void InDocker_FaraRolulAplicatiei_EsteRefuzata()
    {
        // RequireAppRole=true: API-ul din compose se conectează cu acest rol,
        // deci o migrare care nu-l creează ar lăsa API-ul fără bază.
        Assert.NotNull(DbMigrateCommand.Validate(Settings(password: null), isDevelopment: false));
        Assert.NotNull(DbMigrateCommand.Validate(Settings(user: null), isDevelopment: false));
    }

    [Fact]
    public void Local_FaraRolulAplicatiei_EsteAcceptata()
    {
        Assert.Null(DbMigrateCommand.Validate(Settings(user: null, password: null, require: false), isDevelopment: true));
    }

    [Fact]
    public void UtilizatorFaraParola_EsteRefuzat_ChiarSiLocal()
    {
        Assert.NotNull(DbMigrateCommand.Validate(Settings(password: null, require: false), isDevelopment: true));
    }

    [Fact]
    public void ParolaSablon_EsteRefuzataInProductie_AcceptataInDevelopment()
    {
        var settings = Settings(password: "SCHIMBA_MA_MINIM_16_CARACTERE");

        Assert.NotNull(DbMigrateCommand.Validate(settings, isDevelopment: false));
        Assert.Null(DbMigrateCommand.Validate(settings, isDevelopment: true));
    }

    [Fact]
    public void List_NuCereRolulAplicatiei()
    {
        Assert.Null(DbMigrateCommand.Validate(Settings(user: null, password: null, list: true), isDevelopment: false));
    }

    [Fact]
    public void ReadSettings_CitesteConfiguratiaSiArgumentele()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = Owner,
                ["Database:AppUser"]                    = "sgdm_app",
                ["Database:AppPassword"]                = "parolaAplicatiei1234567",
                ["Database:RequireAppRole"]             = "true",
            })
            .Build();

        var settings = DbMigrateCommand.ReadSettings(configuration, new[] { "db:migrate", "--list" });

        Assert.Equal(Owner, settings.ConnectionString);
        Assert.Equal("sgdm_app", settings.AppUser);
        Assert.True(settings.RequireAppRole);
        Assert.True(settings.ListOnly);
    }

    [Theory]
    [InlineData("db:migrate", true)]
    [InlineData("DB:MIGRATE", true)]
    [InlineData("--list", false)]
    [InlineData("storage:recrypt", false)]
    public void IsMigrate_RecunoasteDoarVerbul(string first, bool expected)
    {
        // „docker compose run migrate --list” ar înlocui comanda cu „--list”:
        // fără verb, nu e db:migrate (ar porni API-ul). De aici forma documentată
        // „docker compose run --rm migrate db:migrate --list”.
        Assert.Equal(expected, DbMigrateCommand.IsMigrate(new[] { first }));
    }

    [Theory]
    [InlineData("sgdm_app", true)]
    [InlineData("_intern", true)]
    [InlineData("SGDM_APP", false)]
    [InlineData("sgdm-app", false)]
    [InlineData("sgdm app", false)]
    [InlineData("1app", false)]
    [InlineData("", false)]
    public void NumeleRolului_EsteIdentificatorSimplu(string name, bool valid)
    {
        Assert.Equal(valid, DatabaseRoleProvisioner.RoleNamePattern.IsMatch(name));
    }

    [Fact]
    public void DotEnv_MapeazaRolulAplicatiei()
    {
        Assert.Contains("Database__AppUser", DotEnvLoader.ComposeMapping["POSTGRES_APP_USER"]);
        Assert.Contains(DbMigrateCommand.AppPasswordVariable, DotEnvLoader.ComposeMapping["POSTGRES_APP_PASSWORD"]);
    }
}
