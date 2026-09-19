using MAI.BusinessLogic.Organization;
using MAI.Domain.Enums;
using Xunit;

namespace MAI.Tests;

public class OrgTreeTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    //   A (Direcție) ── B (Secție) ── C (Serviciu)
    //   D (Direcție)
    private static OrgTree Tree() => new(
    [
        new(A, null, null, OrgUnitType.Directie, true, "Direcția A"),
        new(B, A,    null, OrgUnitType.Sectie,   true, "Secția B"),
        new(C, B,    null, OrgUnitType.Serviciu, true, "Serviciul C"),
        new(D, null, null, OrgUnitType.Directie, true, "Direcția D"),
    ]);

    [Fact]
    public void Subarborele_IncludeTotiDescendentii()
    {
        Assert.Equal(new[] { A, B, C }, Tree().Subtree(A).Select(u => u.Id));
        Assert.Equal(new[] { B, C }, Tree().Subtree(A, includeRoot: false).Select(u => u.Id));
    }

    [Fact]
    public void Calea_EsteDeLaVarfLaSubdiviziune()
    {
        Assert.Equal("Direcția A / Secția B / Serviciul C", Tree().PathOf(C));
    }

    [Fact]
    public void MutareaSubPropriulDescendent_EsteRefuzata()
    {
        // A sub C ar crea un ciclu A → B → C → A.
        Assert.NotNull(Tree().ValidatePlacement(A, OrgUnitType.Directie, C));
    }

    [Fact]
    public void NivelulCopilului_TrebuieSaFieMaiMareDecatAlParintelui()
    {
        Assert.NotNull(Tree().ValidatePlacement(null, OrgUnitType.Directie, B));   // Direcție sub Secție
        Assert.NotNull(Tree().ValidatePlacement(null, OrgUnitType.Sectie, B));     // Secție sub Secție
        Assert.Null(Tree().ValidatePlacement(null, OrgUnitType.Serviciu, B));      // Serviciu sub Secție
        Assert.Null(Tree().ValidatePlacement(null, OrgUnitType.Sectie, null));     // nivel de vârf
    }

    [Fact]
    public void MutareaUneiSectiiCuCopiiLaNivelDeServiciu_EsteRefuzata()
    {
        // B devine Serviciu sub D: copilul C (Serviciu) ar ajunge pe același nivel.
        Assert.NotNull(Tree().ValidatePlacement(B, OrgUnitType.Serviciu, D));
    }

    [Fact]
    public void CicluDinBaza_NuBlocheazaParcurgerea()
    {
        // Date corupte manual: X ↔ Y. Parcurgerea trebuie să se oprească.
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var tree = new OrgTree(
        [
            new(x, y, null, OrgUnitType.Directie, true, "X"),
            new(y, x, null, OrgUnitType.Sectie,   true, "Y"),
        ]);

        Assert.Equal(2, tree.Subtree(x).Count);
        Assert.True(tree.IsInSubtree(y, x));
        Assert.NotEmpty(tree.PathOf(x));
    }
}
