using System.Reflection;
using MAI.DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public void UltimaMigrare_ContineTabelaTransferRecipientsSiInvitatia()
    {
        // Modelul-țintă al ultimei migrări trebuie să cunoască schimbările din
        // runda de cerințe 2026-09-17. Dacă cineva adaugă o entitate fără
        // migrare, testul acesta nu o prinde (ar trebui comparat modelul curent
        // cu snapshot-ul, ceea ce cere o bază); prinde însă o migrare nouă scrisă
        // de mână fără model-țintă complet.
        var last = MigrationTypes()
            .Select(t => (Type: t, Id: t.GetCustomAttribute<MigrationAttribute>()!.Id))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Last();

        var migration = (Migration)Activator.CreateInstance(last.Type)!;
        var model     = migration.TargetModel;

        Assert.NotNull(model);
        Assert.NotNull(model!.FindEntityType("MAI.Domain.Entities.TransferRecipient"));

        var user = model.FindEntityType("MAI.Domain.Entities.User");
        Assert.NotNull(user);
        Assert.NotNull(user!.FindProperty("EmailConfirmed"));
        Assert.NotNull(user.FindProperty("InvitationToken"));

        var transfer = model.FindEntityType("MAI.Domain.Entities.FileTransfer");
        Assert.NotNull(transfer);
        Assert.NotNull(transfer!.FindProperty("Category"));
    }
}
