using MAI.Api.Options;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// SMTP „configurat” înseamnă valori reale, nu valorile-șablon din
/// appsettings.json. Altfel serviciul încerca o conexiune spre
/// „YOUR_SMTP_HOST_HERE” la fiecare transfer, iar conturile noi rămâneau
/// neactivate, cu o invitație care nu pleca niciodată.
/// </summary>
public class SmtpOptionsTests
{
    private static SmtpOptions Real() => new()
    {
        Host     = "mail.mai.intern",
        Port     = 465,
        UseSsl   = true,
        From     = "sgdm@mai.intern",
        Username = "sgdm",
        Password = "o-parola-reala-lunga",
    };

    [Fact]
    public void ValoriReale_SuntConsiderateConfigurate()
    {
        var options = Real();

        Assert.True(options.IsConfigured);
        Assert.Empty(options.MissingFields());
    }

    [Fact]
    public void ValorileSablonDinAppsettings_NuSuntConfigurate()
    {
        var options = new SmtpOptions
        {
            Host     = "YOUR_SMTP_HOST_HERE",
            Port     = 465,
            From     = "YOUR_SMTP_FROM_EMAIL_HERE",
            Username = "YOUR_SMTP_USERNAME_HERE",
            Password = "YOUR_SMTP_PASSWORD_HERE",
        };

        Assert.False(options.IsConfigured);
        Assert.Equal(4, options.MissingFields().Count);
    }

    [Fact]
    public void UnSingurCampSablon_AjungeCaSaFieNeconfigurat()
    {
        var options = Real();
        options.Password = "YOUR_SMTP_PASSWORD_HERE";

        Assert.False(options.IsConfigured);
        Assert.Contains(options.MissingFields(), f => f.StartsWith("Smtp:Password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CampGol_NuEsteConfigurat(string host)
    {
        var options = Real();
        options.Host = host;

        Assert.False(options.IsConfigured);
        Assert.Contains("Smtp:Host", options.MissingFields());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void PortInvalid_NuEsteConfigurat(int port)
    {
        var options = Real();
        options.Port = port;

        Assert.False(options.IsConfigured);
        Assert.Contains("Smtp:Port", options.MissingFields());
    }
}
