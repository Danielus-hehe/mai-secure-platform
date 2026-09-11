using MAI.BusinessLogic.Security;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Protecția la reluare pentru codurile TOTP (RFC 6238, secțiunea 5.2): un cod
/// acceptat o dată nu mai trece, chiar dacă e încă în fereastra de toleranță.
/// </summary>
public class TotpReplayTests
{
    private static readonly TotpService Service = new(new TwoFactorOptions
    {
        Issuer                   = "SGDM MAI",
        Digits                   = 6,
        PeriodSeconds            = 30,
        WindowSteps              = 1,
        RecoveryCodeCount        = 10,
        ChallengeLifetimeSeconds = 180,
        MaxChallengeAttempts     = 5,
        EncryptionKey            = "dGVzdC1jaGVpZS1kZS0zMi1vY3RldGktcGVudHJ1LXRlc3Rl",
    });

    private static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    private static string CodeFor(string secret, long step) =>
        Service.ComputeCode(TotpService.Base32Decode(secret), step);

    [Fact]
    public void Verify_CodCurent_EsteAcceptatSiIntoarceIntervalul()
    {
        var secret = Service.GenerateSecret();
        var step   = CurrentStep();

        var result = Service.Verify(secret, CodeFor(secret, step), lastUsedStep: null);

        Assert.Equal(TotpVerificationStatus.Accepted, result.Status);
        Assert.Equal(step, result.Step);
    }

    [Fact]
    public void Verify_AcelasiCodDeDouaOri_ADouaOaraEsteRespins()
    {
        var secret = Service.GenerateSecret();
        var code   = CodeFor(secret, CurrentStep());

        var first  = Service.Verify(secret, code, lastUsedStep: null);
        var second = Service.Verify(secret, code, lastUsedStep: first.Step);

        Assert.True(first.IsAccepted);
        Assert.Equal(TotpVerificationStatus.Replayed, second.Status);
        Assert.False(second.IsAccepted);
    }

    [Fact]
    public void Verify_CodMaiVechiDecatUltimulFolosit_EsteRespins()
    {
        // Codul intervalului anterior e încă în fereastra de toleranță (±1), dar
        // după ce s-a folosit codul curent nu mai are voie să treacă.
        var secret = Service.GenerateSecret();
        var step   = CurrentStep();

        var result = Service.Verify(secret, CodeFor(secret, step - 1), lastUsedStep: step);

        Assert.Equal(TotpVerificationStatus.Replayed, result.Status);
    }

    [Fact]
    public void Verify_CodDinIntervalulUrmator_EsteAcceptatDupaCelCurent()
    {
        var secret = Service.GenerateSecret();
        var step   = CurrentStep();

        var result = Service.Verify(secret, CodeFor(secret, step + 1), lastUsedStep: step);

        Assert.True(result.IsAccepted);
        Assert.Equal(step + 1, result.Step);
    }

    [Fact]
    public void Verify_CodGresit_EsteInvalid()
    {
        var secret = Service.GenerateSecret();
        var code   = CodeFor(secret, CurrentStep());
        var wrong  = code == "000000" ? "111111" : "000000";

        var result = Service.Verify(secret, wrong, lastUsedStep: null);

        Assert.Equal(TotpVerificationStatus.Invalid, result.Status);
        Assert.Null(result.Step);
    }
}
