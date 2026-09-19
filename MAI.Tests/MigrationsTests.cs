using System.Reflection;
using MAI.DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Migrările EF sunt descoperite prin reflecție, după atributele
/// [Migration] și [DbContext] din fișierele .Designer.cs. O migrare fără ele
/// compilează, dar nu se aplică niciodată — exact ce s-a întâmplat cu cele patru
/// migrări din 2026-09-17/18, care au lăsat baza fără tabela TransferRecipients
/// și fără coloana EmailConfirmed.
///
/// Testele rulează fără bază de date: verifică doar ce vede EF în assembly.
/// </summary>
public class MigrationsTests
{
    private static readonly Assembly DataAccessAssembly = typeof(AppDbContext).Assembly;

    private static IReadOnlyList<Type> MigrationTypes() =>
        DataAccessAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Migration).IsAssignableFrom(t))
            .ToList();

    [Fact]
    public void FiecareMigrare_AreAtributulMigration()
    {
        var fara = MigrationTypes()
            .Where(t => t.GetCustomAttribute<MigrationAttribute>() is null)
            .Select(t => t.Name)
            .ToList();

        Assert.True(fara.Count == 0,
            "Migrări fără [Migration] (lipsește .Designer.cs, EF nu le aplică): " + string.Join(", ", fara));
    }

    [Fact]
    public void FiecareMigrare_EsteLegataDeAppDbContext()
    {
        var gresite = MigrationTypes()
            .Where(t => t.GetCustomAttribute<DbContextAttribute>()?.ContextType != typeof(AppDbContext))
            .Select(t => t.Name)
            .ToList();

        Assert.True(gresite.Count == 0,
            "Migrări fără [DbContext(typeof(AppDbContext))]: " + string.Join(", ", gresite));
    }

    [Fact]
    public void IdentificatoriiMigrarilor_SuntUnici()
    {
        var duplicate = MigrationTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .Where(id => id is not null)
            .Select(id => id!.Split('_')[0])
            .GroupBy(timestamp => timestamp)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicate.Count == 0,
            "Timestamp de migrare folosit de mai multe ori: " + string.Join(", ", duplicate));
    }

    [Fact]
    public void ExistaUnSingurSnapshot_PentruAppDbContext()
    {
        var snapshots = DataAccessAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ModelSnapshot).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(AppDbContext))
            .ToList();

        Assert.Single(snapshots);
    }

    [Fact]
    public void UltimaMigrare_ContineStructuraOrganizatorica_SiDocumenteleInterne()
    {
        // Modelul-țintă al ultimei migrări trebuie să conțină tot ce au adus
        // rundele 2026-09-19 (dovadă de primire per destinatar, forward, ștergere
        // logică) și 2026-09-20 (structură, documente interne, resetare parolă).
        var last = MigrationTypes()
            .Select(t => (Type: t, Id: t.GetCustomAttribute<MigrationAttribute>()!.Id))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Last();

        var migration = (Migration)Activator.CreateInstance(last.Type)!;
        var model     = migration.TargetModel;

        Assert.NotNull(model);

        var recipient = model!.FindEntityType("MAI.Domain.Entities.TransferRecipient");
        Assert.NotNull(recipient);
        Assert.NotNull(recipient!.FindProperty("DownloadedAt"));
        Assert.NotNull(recipient.FindProperty("SignatureValid"));

        var transfer = model.FindEntityType("MAI.Domain.Entities.FileTransfer");
        Assert.NotNull(transfer);
        Assert.NotNull(transfer!.FindProperty("AllowForward"));
        Assert.NotNull(transfer.FindProperty("DeletedAt"));
        Assert.Null(transfer.FindProperty("RecipientId"));

        var user = model.FindEntityType("MAI.Domain.Entities.User");
        Assert.NotNull(user);
        Assert.NotNull(user!.FindProperty("OrgUnitId"));
        Assert.NotNull(user.FindProperty("PasswordResetToken"));
        Assert.Null(user.FindProperty("Department"));

        Assert.NotNull(model.FindEntityType("MAI.Domain.Entities.OrgUnit"));
        Assert.NotNull(model.FindEntityType("MAI.Domain.Entities.InternalDocument"));
        Assert.NotNull(model.FindEntityType("MAI.Domain.Entities.InternalDocumentRecipient"));
        Assert.NotNull(model.FindEntityType("MAI.Domain.Entities.InternalDocumentTarget"));
    }

    [Fact]
    public void Snapshotul_CorespundeModeluluiCurent()
    {
        // Echivalentul lui „dotnet ef migrations has-pending-model-changes”, fără
        // bază de date: compară modelul din AppDbContext (configurările Fluent)
        // cu snapshot-ul din Migrations. O entitate sau o proprietate schimbată
        // fără migrare — sau o migrare scrisă de mână cu snapshot greșit — apare
        // aici ca listă de operații pe care EF le-ar genera.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=sgdm_design;Username=x;Password=x")
            .Options;

        using var context = new AppDbContext(options);

        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        IModel snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutable)
            snapshotModel = mutable.FinalizeModel();
        snapshotModel = context.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshotModel, designTime: true, validationLogger: null);

        var currentModel = context.GetService<IDesignTimeModel>().Model;

        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            currentModel.GetRelationalModel());

        Assert.True(differences.Count == 0,
            "Modelul diferă de snapshot. Operații în așteptare:\n  " +
            string.Join("\n  ", differences.Select(Describe)));
    }

    private static string Describe(MigrationOperation op) => op switch
    {
        ColumnOperation c      => $"{op.GetType().Name} {c.Table}.{c.Name}",
        DropColumnOperation d  => $"{op.GetType().Name} {d.Table}.{d.Name}",
        CreateIndexOperation i => $"{op.GetType().Name} {i.Table}.{i.Name}",
        DropIndexOperation i   => $"{op.GetType().Name} {i.Table}.{i.Name}",
        AddForeignKeyOperation f  => $"{op.GetType().Name} {f.Table}.{f.Name}",
        DropForeignKeyOperation f => $"{op.GetType().Name} {f.Table}.{f.Name}",
        TableOperation t       => $"{op.GetType().Name} {t.Name}",
        _                      => op.GetType().Name,
    };
}
