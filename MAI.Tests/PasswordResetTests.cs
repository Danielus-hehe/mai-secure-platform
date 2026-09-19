using MAI.Api.Configuration;
using MAI.Api.Options;
using MAI.Api.Services;
using MAI.Domain.Entities;
using Xunit;

namespace MAI.Tests;

public class PasswordResetTests
{
    [Fact]
    public void Tokenul_Are256DeBiti_SiEsteSigurInUrl()
    {
        var token = UrlSafeTokens.Generate();

        Assert.Equal(43, token.Length);   // 32 de octeți în Base64Url fără padding
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(token, UrlSafeTokens.Generate());
    }

    [Fact]
    public void InBaza_SeStocheazaDoarAmprenta()
    {
        var token = UrlSafeTokens.Generate();
        var hash  = UrlSafeTokens.Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.NotEqual(token, hash);
        Assert.Equal(hash, UrlSafeTokens.Hash(token));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1441)]
    public void ValabilitateInafaraLimitelor_OpresPornirea(int minutes)
    {
        Assert.Throws<InvalidOperationException>(new PasswordResetOptions { TokenMinutes = minutes }.Validate);
    }

    [Fact]
    public void VariabilaDinEnv_AjungeInSectiunePasswordReset()
    {
        Assert.True(DotEnvLoader.ComposeMapping.TryGetValue("PASSWORD_RESET_TOKEN_MINUTES", out var targets));
        Assert.Contains("PasswordReset__TokenMinutes", targets!);
    }

    [Fact]
    public void StergereaCheilor_GolesteTotMaterialulE2ee()
    {
        // Resetarea fără parola veche nu poate re-împacheta cheile private:
        // tot materialul trebuie să dispară, altfel contul rămâne în impas.
        var user = new User
        {
            PublicKeyEncryption     = "pk",
            PublicKeySigning        = "ps",
            EncryptedPrivateBundle  = "blob",
            KeyDerivationSalt       = "salt",
            KeyDerivationIterations = 600_000,
            KeyWrapIv               = "iv",
            CryptoSuite             = "suite",
            KeysCreatedAt           = DateTime.UtcNow,
        };

        Assert.True(user.ClearEncryptionKeys());
        Assert.False(user.HasKeys);
        Assert.Null(user.EncryptedPrivateBundle);
        Assert.Null(user.KeyDerivationSalt);
        Assert.Null(user.KeysCreatedAt);
        Assert.False(user.ClearEncryptionKeys());   // a doua oară: nu mai era nimic
    }
}
