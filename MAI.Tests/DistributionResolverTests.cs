using MAI.BusinessLogic.Organization;
using MAI.Domain.Enums;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Structura de test:
///
///   DGP (Direcție)                 șef: Sef
///   ├── Secția investigații        șef: SefSectie
///   │   └── Serviciul analiză      șef: SefServiciu
///   └── Secția pază                fără șef
///   DTI (Direcție)                 șef: SefDti
///
/// Membri: Ana (DGP), Bob (Secția investigații), Cara (Serviciul analiză),
/// Dan (Secția pază), Eva (DTI), Fost (DGP, cont dezactivat).
/// </summary>
public class DistributionResolverTests
{
    private static readonly Guid Dgp = Guid.NewGuid(), SectInv = Guid.NewGuid(), ServAn = Guid.NewGuid(),
                                 SectPaza = Guid.NewGuid(), Dti = Guid.NewGuid();

    private static readonly Guid Sef = Guid.NewGuid(), SefSectie = Guid.NewGuid(), SefServiciu = Guid.NewGuid(),
                                 SefDti = Guid.NewGuid(), Ana = Guid.NewGuid(), Bob = Guid.NewGuid(),
                                 Cara = Guid.NewGuid(), Dan = Guid.NewGuid(), Eva = Guid.NewGuid(),
                                 Fost = Guid.NewGuid(), Admin = Guid.NewGuid();

    private static readonly OrgTree Tree = new(
    [
        new(Dgp,      null,    Sef,         OrgUnitType.Directie, true, "DGP"),
        new(SectInv,  Dgp,     SefSectie,   OrgUnitType.Sectie,   true, "Secția investigații"),
        new(ServAn,   SectInv, SefServiciu, OrgUnitType.Serviciu, true, "Serviciul analiză"),
        new(SectPaza, Dgp,     null,        OrgUnitType.Sectie,   true, "Secția pază"),
        new(Dti,      null,    SefDti,      OrgUnitType.Directie, true, "DTI"),
    ]);

    private static readonly List<OrgMember> Members =
    [
        new(Sef, Dgp, true), new(SefSectie, SectInv, true), new(SefServiciu, ServAn, true),
        new(SefDti, Dti, true), new(Ana, Dgp, true), new(Bob, SectInv, true), new(Cara, ServAn, true),
        new(Dan, SectPaza, true), new(Eva, Dti, true), new(Fost, Dgp, false), new(Admin, null, true),
    ];

    private static DistributionResult Resolve(Guid author, DistributionMode mode,
        Guid[]? units = null, Guid[]? users = null, bool includeSub = true, bool admin = false) =>
        DistributionResolver.Resolve(author, admin,
            new DistributionRequest(mode, units ?? [], users ?? [], includeSub), Tree, Members);

    // ── Subdiviziunea mea ────────────────────────────────────────────────────

    [Fact]
    public void SubdiviziuneaMea_CuprindeTotArborele_FaraAutor_SiFaraConturiDezactivate()
    {
        var r = Resolve(Sef, DistributionMode.MyUnitTree);

        Assert.True(r.Success);
        Assert.Equal(
            new[] { Ana, Bob, Cara, Dan, SefSectie, SefServiciu }.OrderBy(x => x),
            r.RecipientIds.OrderBy(x => x));
        Assert.DoesNotContain(Sef, r.RecipientIds);
        Assert.DoesNotContain(Fost, r.RecipientIds);
        Assert.DoesNotContain(Eva, r.RecipientIds);
    }

    [Fact]
    public void SefulDeSectie_DistribuieDoarInSectiaLui()
    {
        var r = Resolve(SefSectie, DistributionMode.MyUnitTree);

        Assert.True(r.Success);
        Assert.Equal(new[] { Bob, Cara, SefServiciu }.OrderBy(x => x), r.RecipientIds.OrderBy(x => x));
    }

    [Fact]
    public void FaraSubdiviziuneCondusa_NuPoateDistribuiSubordonatilor()
    {
        var r = Resolve(Ana, DistributionMode.MyUnitTree);

        Assert.False(r.Success);
        Assert.Contains("conduceți", r.Error);
    }

    // ── Subordonați direcți ──────────────────────────────────────────────────

    [Fact]
    public void SubordonatiiDirecti_SuntMembriiUnitatii_SiSefiiSubunitatilorImediate()
    {
        // Ana e în DGP; șeful secției de investigații e subordonat direct.
        // Bob și Cara sunt subordonații ȘEFULUI DE SECȚIE, nu ai directorului.
        // Secția pază nu are șef, deci Dan nu e subordonat direct al nimănui
        // aici - decizie documentată: distribuția nu „sare” un nivel.
        var r = Resolve(Sef, DistributionMode.DirectSubordinates);

        Assert.True(r.Success);
        Assert.Equal(new[] { Ana, SefSectie }.OrderBy(x => x), r.RecipientIds.OrderBy(x => x));
    }

    // ── Șefii subunităților ──────────────────────────────────────────────────

    [Fact]
    public void DoarSefiiSubunitatilor_IgnoraSubunitatileFaraSef()
    {
        var r = Resolve(Sef, DistributionMode.UnitHeads);

        Assert.True(r.Success);
        Assert.Equal(new[] { SefSectie, SefServiciu }.OrderBy(x => x), r.RecipientIds.OrderBy(x => x));
    }

    [Fact]
    public void SefFaraSubunitati_NuAreModulSefilor()
    {
        var caps = DistributionResolver.CapabilitiesFor(SefServiciu, isAdmin: false, Tree);

        Assert.DoesNotContain(DistributionMode.UnitHeads, caps.Modes);
        Assert.Contains(DistributionMode.MyUnitTree, caps.Modes);
    }

    // ── Subdiviziuni selectate ───────────────────────────────────────────────

    [Fact]
    public void SubdiviziuniSelectate_CuSauFaraSubunitati()
    {
        var cu   = Resolve(Sef, DistributionMode.SelectedUnits, units: [SectInv], includeSub: true);
        var fara = Resolve(Sef, DistributionMode.SelectedUnits, units: [SectInv], includeSub: false);

        Assert.Equal(new[] { Bob, Cara, SefSectie, SefServiciu }.OrderBy(x => x), cu.RecipientIds.OrderBy(x => x));
        Assert.Equal(new[] { Bob, SefSectie }.OrderBy(x => x), fara.RecipientIds.OrderBy(x => x));
    }

    [Fact]
    public void SubdiviziuneDinAfaraSubordinii_EsteRefuzataPeServer()
    {
        // Interfața nu îi arată DTI, dar o cerere construită manual trebuie oprită.
        var r = Resolve(Sef, DistributionMode.SelectedUnits, units: [Dti]);

        Assert.False(r.Success);
        Assert.Contains("DTI", r.Error);
    }

    [Fact]
    public void Administratorul_PoateAlegeOriceSubdiviziune()
    {
        var r = Resolve(Admin, DistributionMode.SelectedUnits, units: [Dti], admin: true);

        Assert.True(r.Success);
        Assert.Equal(new[] { Eva, SefDti }.OrderBy(x => x), r.RecipientIds.OrderBy(x => x));
    }

    // ── Persoane anume și toată instituția ───────────────────────────────────

    [Fact]
    public void PersoaneAnume_SuntPermiseOricui()
    {
        var r = Resolve(Ana, DistributionMode.SpecificUsers, users: [Eva, Dan]);

        Assert.True(r.Success);
        Assert.Equal(new[] { Dan, Eva }.OrderBy(x => x), r.RecipientIds.OrderBy(x => x));
    }

    [Fact]
    public void PersoaneAnume_RefuzaConturileDezactivate()
    {
        var r = Resolve(Ana, DistributionMode.SpecificUsers, users: [Fost]);

        Assert.False(r.Success);
    }

    [Fact]
    public void TotaInstitutia_EsteDoarPentruAdministrator()
    {
        var sef   = Resolve(Sef, DistributionMode.WholeInstitution);
        var admin = Resolve(Admin, DistributionMode.WholeInstitution, admin: true);

        Assert.False(sef.Success);
        Assert.True(admin.Success);
        Assert.Equal(Members.Count(m => m.IsActive) - 1, admin.RecipientIds.Count);   // fără autor
        Assert.DoesNotContain(Fost, admin.RecipientIds);
    }

    [Fact]
    public void ModNedefinit_EsteRefuzat()
    {
        var r = Resolve(Admin, (DistributionMode)99, admin: true);

        Assert.False(r.Success);
    }
}
