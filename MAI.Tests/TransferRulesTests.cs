using MAI.BusinessLogic.Transfers;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Regulile de stare ale transferurilor: dovada de primire per destinatar,
/// expirarea, politica de forward și validarea datelor venite din formular.
/// Toate sunt funcții pure — nu cer bază de date.
/// </summary>
public class TransferRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Sender = Guid.NewGuid();
    private static readonly Guid Alice  = Guid.NewGuid();
    private static readonly Guid Bob    = Guid.NewGuid();

    private static readonly TransferPolicyOptions Policy = new()
    {
        DefaultExpiryDays = 7,
        MaxExpiryDays     = 30,
        MaxRecipients     = 20,
    };

    private static FileTransfer Transfer(
        TransferStatus status = TransferStatus.Pending,
        DateTime? expiresAt = null,
        bool allowForward = false,
        DateTime? deletedAt = null,
        params TransferRecipient[] recipients)
    {
        var t = new FileTransfer
        {
            SenderId     = Sender,
            Status       = status,
            ExpiresAt    = expiresAt ?? Now.AddDays(3),
            AllowForward = allowForward,
            DeletedAt    = deletedAt,
        };
        foreach (var r in recipients) t.Recipients.Add(r);
        return t;
    }

    private static TransferRecipient Rcpt(Guid user, DateTime? downloadedAt = null) =>
        new() { UserId = user, DownloadedAt = downloadedAt, EncryptedKeyForUser = "AAAA" };

    // ── Dovada de primire ────────────────────────────────────────────────────

    [Fact]
    public void PrimaConfirmare_NuMarcheazaTransferulCaDescarcatDeToti()
    {
        // Bug-ul vechi: confirmarea oricărui destinatar punea Downloaded pe tot
        // transferul, deși ceilalți nu îl deschiseseră.
        var recipients = new[] { Rcpt(Alice, Now), Rcpt(Bob) };

        var status = TransferRules.AggregateStatus(TransferStatus.Pending, recipients);

        Assert.Equal(TransferStatus.Pending, status);
    }

    [Fact]
    public void TotiAuConfirmat_TransferulDevineDownloaded()
    {
        var recipients = new[] { Rcpt(Alice, Now), Rcpt(Bob, Now.AddMinutes(5)) };

        Assert.Equal(TransferStatus.Downloaded,
            TransferRules.AggregateStatus(TransferStatus.Pending, recipients));
    }

    [Fact]
    public void DestinatarNouPrinForward_ReaduceTransferulInPending()
    {
        var recipients = new[] { Rcpt(Alice, Now), Rcpt(Bob) };

        Assert.Equal(TransferStatus.Pending,
            TransferRules.AggregateStatus(TransferStatus.Downloaded, recipients));
    }

    [Theory]
    [InlineData(TransferStatus.Expired)]
    [InlineData(TransferStatus.Revoked)]
    public void StarileTerminale_NuSeRecalculeaza(TransferStatus terminal)
    {
        var recipients = new[] { Rcpt(Alice, Now) };

        Assert.Equal(terminal, TransferRules.AggregateStatus(terminal, recipients));
    }

    [Fact]
    public void FaraDestinatari_RamanePending()
    {
        Assert.Equal(TransferStatus.Pending,
            TransferRules.AggregateStatus(TransferStatus.Pending, Array.Empty<TransferRecipient>()));
    }

    // ── Expirare ─────────────────────────────────────────────────────────────

    [Fact]
    public void PendingTrecutDeTermen_ApareCaExpired()
    {
        var t = Transfer(expiresAt: Now.AddMinutes(-1));

        Assert.Equal(TransferStatus.Expired, TransferRules.EffectiveStatus(t, Now));
        Assert.False(TransferRules.IsContentAvailable(t, Now));
    }

    [Fact]
    public void DownloadedTrecutDeTermen_RamaneDownloaded()
    {
        // Dovada „primit de toți” nu e suprascrisă de trecerea timpului.
        var t = Transfer(TransferStatus.Downloaded, expiresAt: Now.AddDays(-1));

        Assert.Equal(TransferStatus.Downloaded, TransferRules.EffectiveStatus(t, Now));
        Assert.False(TransferRules.IsContentAvailable(t, Now));
    }

    [Fact]
    public void RevokedTrecutDeTermen_RamaneRevoked()
    {
        var t = Transfer(TransferStatus.Revoked, expiresAt: Now.AddDays(-1));

        Assert.Equal(TransferStatus.Revoked, TransferRules.EffectiveStatus(t, Now));
    }

    [Fact]
    public void TransferSters_NuMaiEsteDisponibil()
    {
        var t = Transfer(deletedAt: Now.AddHours(-1));

        Assert.False(TransferRules.IsContentAvailable(t, Now));
    }

    [Fact]
    public void DownloadedInTermen_PoateFiDescarcatDinNou()
    {
        var t = Transfer(TransferStatus.Downloaded);

        Assert.True(TransferRules.IsContentAvailable(t, Now));
    }

    // ── Forward ──────────────────────────────────────────────────────────────

    [Fact]
    public void Expeditorul_PoateRedirectionaMereu()
    {
        var t = Transfer(allowForward: false);

        Assert.True(TransferRules.CanForward(t, Sender, isRecipient: false, Now));
    }

    [Fact]
    public void Destinatarul_NuPoateRedirectionaFaraPermisiune()
    {
        var t = Transfer(allowForward: false);

        Assert.False(TransferRules.CanForward(t, Alice, isRecipient: true, Now));
    }

    [Fact]
    public void Destinatarul_PoateRedirectionaCuPermisiune()
    {
        var t = Transfer(allowForward: true);

        Assert.True(TransferRules.CanForward(t, Alice, isRecipient: true, Now));
    }

    [Fact]
    public void UnStrain_NuPoateRedirectionaNiciCuPermisiune()
    {
        var t = Transfer(allowForward: true);

        Assert.False(TransferRules.CanForward(t, Bob, isRecipient: false, Now));
    }

    [Fact]
    public void TransferRetras_NuMaiPoateFiRedirectionat()
    {
        var t = Transfer(TransferStatus.Revoked, allowForward: true);

        Assert.False(TransferRules.CanForward(t, Sender, isRecipient: false, Now));
    }

    // ── Retragere ────────────────────────────────────────────────────────────

    [Fact]
    public void Retragerea_EstePermisaCatTimpUnDestinatarNuADescarcat()
    {
        var t = Transfer(recipients: [Rcpt(Alice, Now), Rcpt(Bob)]);

        Assert.True(TransferRules.CanRevoke(t, Sender, Now));
        Assert.False(TransferRules.CanRevoke(t, Alice, Now));
    }

    [Fact]
    public void Retragerea_NuEstePermisaDupaCeTotiAuDescarcat()
    {
        var t = Transfer(TransferStatus.Downloaded);

        Assert.False(TransferRules.CanRevoke(t, Sender, Now));
    }

    // ── Data de expirare ceruta ──────────────────────────────────────────────

    [Fact]
    public void FaraData_SeFolosesteValabilitateaImplicita()
    {
        var r = TransferRules.ResolveExpiry(null, Policy, Now);

        Assert.True(r.Success);
        Assert.Equal(Now.AddDays(7), r.Value);
    }

    [Fact]
    public void DataInTrecut_EsteRefuzata()
    {
        var r = TransferRules.ResolveExpiry(Now.AddMinutes(-5), Policy, Now);

        Assert.False(r.Success);
    }

    [Fact]
    public void DataPesteMaxim_EsteRefuzata()
    {
        var r = TransferRules.ResolveExpiry(Now.AddDays(31), Policy, Now);

        Assert.False(r.Success);
        Assert.Contains("30", r.Error);
    }

    [Fact]
    public void DataLaLimitaMaxima_EsteAcceptata()
    {
        var r = TransferRules.ResolveExpiry(Now.AddDays(30), Policy, Now);

        Assert.True(r.Success);
    }

    [Fact]
    public void DataFaraFusOrar_EsteTratataCaUtc()
    {
        // O valoare fără Kind nu se interpretează după ora locală a serverului:
        // același request ar da rezultate diferite pe Windows și în container.
        var unspecified = DateTime.SpecifyKind(Now.AddDays(2), DateTimeKind.Unspecified);

        var r = TransferRules.ResolveExpiry(unspecified, Policy, Now);

        Assert.True(r.Success);
        Assert.Equal(DateTimeKind.Utc, r.Value.Kind);
        Assert.Equal(Now.AddDays(2), r.Value);
    }

    // ── Categorie ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    [InlineData(42, false)]
    public void Categoria_EsteValidataCuEnumIsDefined(int value, bool expected)
    {
        Assert.Equal(expected, TransferRules.IsValidCategory((TransferCategory)value));
    }
}
