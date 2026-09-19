using MAI.BusinessLogic.Organization;
using Xunit;

namespace MAI.Tests;

/// <summary>Alegerea rangului pentru un nivel nou de structură.</summary>
public class OrgLevelRulesTests
{
    private static readonly int[] Predefined = [100, 200, 300];   // Direcție, Secție, Serviciu

    [Fact]
    public void NivelIntreDouaExistente_PrimesteRangulDeMijloc()
    {
        // „Departament” între Direcție și Secție.
        Assert.Equal(150, OrgLevelRules.RankAfter(Predefined, afterRank: 100));
    }

    [Fact]
    public void NivelSubUltimul_PrimesteUnPasInPlus()
    {
        // „Birou” sub Serviciu.
        Assert.Equal(400, OrgLevelRules.RankAfter(Predefined, afterRank: 300));
    }

    [Fact]
    public void NivelDeasupraTuturor_EsteInainteaPrimului()
    {
        Assert.Equal(50, OrgLevelRules.RankAfter(Predefined, afterRank: null));
    }

    [Fact]
    public void FaraLoc_IntreDouaRanguriConsecutive_IntoarceNull()
    {
        Assert.Null(OrgLevelRules.RankAfter([100, 101], afterRank: 100));
    }

    [Fact]
    public void NivelDeReferintaInexistent_IntoarceNull()
    {
        Assert.Null(OrgLevelRules.RankAfter(Predefined, afterRank: 250));
    }

    [Fact]
    public void PrimulNivel_IntrOStructuraGoala()
    {
        Assert.Equal(100, OrgLevelRules.RankAfter([], afterRank: null));
    }
}
