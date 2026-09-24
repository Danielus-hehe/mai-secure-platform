using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Un rând de audit prea lung nu are voie să anuleze operația pe care o
/// consemnează. Înainte, un transfer către 20 de destinatari cu nume lungi
/// producea un Details de peste 1024 de caractere, iar trimiterea eșua cu 500.
/// </summary>
public class AuditLogLimitsTests
{
    /// <summary>
    /// Contextul real, cu modelul real (HasMaxLength din AuditLogConfiguration).
    /// Nu se deschide nicio conexiune: construirea modelului și ChangeTracker-ul
    /// nu au nevoie de server.
    /// </summary>
    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_in_unit_tests")
            .Options);

    [Fact]
    public void DetailsPreaLung_EsteTaiatLaLimitaColoanei_CuMarcaj()
    {
        using var db = CreateContext();
        var row = new AuditLog
        {
            Username  = "u",
            Action    = AuditAction.FileUpload,
            Details   = new string('x', 5000),
            IpAddress = "10.0.0.1",
        };
        db.AuditLogs.Add(row);

        AuditLogLimits.Apply(db.ChangeTracker);

        Assert.Equal(1024, row.Details.Length);
        Assert.EndsWith(AuditLogLimits.TruncationMarker, row.Details);
    }

    [Fact]
    public void UsernameSiIp_SuntTaiateLaLimitaLor()
    {
        using var db = CreateContext();
        var row = new AuditLog
        {
            Username  = new string('n', 300),
            Details   = "ok",
            IpAddress = new string('9', 200),
        };
        db.AuditLogs.Add(row);

        AuditLogLimits.Apply(db.ChangeTracker);

        Assert.Equal(128, row.Username.Length);
        Assert.Equal(64, row.IpAddress.Length);
    }

    [Fact]
    public void TextInLimita_RamaneNeatins()
    {
        using var db = CreateContext();
        var details = new string('y', 1024);
        var row = new AuditLog { Username = "u", Details = details, IpAddress = "::1" };
        db.AuditLogs.Add(row);

        AuditLogLimits.Apply(db.ChangeTracker);

        Assert.Same(details, row.Details);
    }

    [Fact]
    public void Fit_NuRupePerechiSurogat()
    {
        // 9 caractere + un emoji (2 unități UTF-16) = 11. Tăiat la 11 cu marcaj,
        // tăietura ar cădea între cele două jumătăți ale emoji-ului.
        var value = new string('a', 9) + "\U0001F4C4" + "restul";

        var fitted = AuditLogLimits.Fit(value, 11);

        Assert.True(fitted.Length <= 11);
        Assert.False(char.IsHighSurrogate(fitted[^2]));
        Assert.EndsWith(AuditLogLimits.TruncationMarker, fitted);
    }
}
