using MAI.Api.Configuration;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// .env-ul comun trebuie citit la fel ca de Docker Compose, fără să strice o
/// configurare locală care funcționează deja.
/// </summary>
public class DotEnvLoaderTests
{
    private static (Dictionary<string, string> env, DotEnvLoader.Result result) Run(
        string content, Dictionary<string, string>? existing = null)
    {
        var env = existing ?? new Dictionary<string, string>();
        var result = DotEnvLoader.Apply(
            ".env",
            DotEnvLoader.Parse(content),
            key => env.TryGetValue(key, out var v) ? v : null,
            (key, value) => env[key] = value);
        return (env, result);
    }

    [Fact]
    public void Parse_IgnoraComentariiSiLiniiGoale_SiScoateGhilimelele()
    {
        var values = DotEnvLoader.Parse(
            "# comentariu\n\nA=1\r\nB=\"cu spatii\"\nC='simplu'\nexport D=4\nE=valoare # comentariu\n");

        Assert.Equal("1", values["A"]);
        Assert.Equal("cu spatii", values["B"]);
        Assert.Equal("simplu", values["C"]);
        Assert.Equal("4", values["D"]);
        Assert.Equal("valoare", values["E"]);
    }

    [Fact]
    public void Parse_PastreazaDiezulDinParola()
    {
        // Un # lipit de text face parte din valoare (parole, connection string-uri).
        var values = DotEnvLoader.Parse("MAI_SMTP_PASSWORD=abc#def\nX=a=b=c\n");

        Assert.Equal("abc#def", values["MAI_SMTP_PASSWORD"]);
        Assert.Equal("a=b=c", values["X"]);
    }

    [Fact]
    public void CheileCompose_SuntTraduseInCheileAspNet()
    {
        var (env, _) = Run("SMTP_HOST=smtp.gmail.com\nDB_CONNECTION_STRING=Host=db\nFRONTEND_ORIGIN=http://localhost:5173\n");

        Assert.Equal("smtp.gmail.com", env["Smtp__Host"]);
        Assert.Equal("Host=db", env["ConnectionStrings__DefaultConnection"]);
        Assert.Equal("http://localhost:5173", env["Cors__AllowedOrigins__0"]);
        Assert.Equal("http://localhost:5173", env["Frontend__BaseUrl"]);
        Assert.Equal("smtp.gmail.com", env["SMTP_HOST"]);
    }

    [Fact]
    public void VariabilaDejaSetata_NuEsteSuprascrisa()
    {
        var (env, result) = Run(
            "MAI_JWT_KEY=din-fisier-o-cheie-reala\n",
            new Dictionary<string, string> { ["MAI_JWT_KEY"] = "din-mediu" });

        Assert.Equal("din-mediu", env["MAI_JWT_KEY"]);
        Assert.Equal(0, result.Applied);
    }

    [Theory]
    [InlineData("SMTP_HOST=\n")]
    [InlineData("MAI_ARGON2_PEPPER=GENERATI_CU_openssl_rand_base64_32\n")]
    [InlineData("MINIO_ROOT_PASSWORD=SCHIMBA_MA_MINIM_8_CARACTERE\n")]
    public void ValoriGoaleSauSablon_SuntIgnorate(string content)
    {
        // Un .env copiat din .env.example și necompletat nu are voie să strice
        // configurarea locală. Pepper-ul pe valoarea-șablon ar invalida toate parolele.
        var (env, result) = Run(content);

        Assert.Empty(env);
        Assert.Equal(0, result.Applied);
    }
}
