using MAI.Api.Configuration;
using MAI.BusinessLogic.Transfers;
using Xunit;

namespace MAI.Tests;

public class TransferPolicyOptionsTests
{
    [Fact]
    public void ValorileImplicite_SuntValide()
    {
        new TransferPolicyOptions().Validate();
    }

    [Theory]
    [InlineData(0, 30, 20)]    // implicit zero
    [InlineData(7, 0, 20)]     // maxim zero
    [InlineData(7, 400, 20)]   // maxim peste un an
    [InlineData(40, 30, 20)]   // implicit peste maxim
    [InlineData(7, 30, 0)]     // niciun destinatar
    [InlineData(7, 30, 500)]   // prea mulți destinatari
    public void ValoriInvalide_OpresPornirea(int defaultDays, int maxDays, int maxRecipients)
    {
        var options = new TransferPolicyOptions
        {
            DefaultExpiryDays = defaultDays,
            MaxExpiryDays     = maxDays,
            MaxRecipients     = maxRecipients,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("TRANSFER_DEFAULT_EXPIRY_DAYS", "Transfers__DefaultExpiryDays")]
    [InlineData("TRANSFER_MAX_EXPIRY_DAYS", "Transfers__MaxExpiryDays")]
    [InlineData("TRANSFER_MAX_RECIPIENTS", "Transfers__MaxRecipients")]
    public void VariabileleDinEnv_AjungInSectiuneaTransfers(string envKey, string configKey)
    {
        // Aceeași mapare ca în docker-compose.yml, pentru rularea cu dotnet run.
        Assert.True(DotEnvLoader.ComposeMapping.TryGetValue(envKey, out var targets));
        Assert.Contains(configKey, targets!);
    }
}
