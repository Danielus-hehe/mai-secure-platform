using MAI.BusinessLogic.Ldap;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Partea din integrarea cu Active Directory care nu are nevoie de un
/// controler de domeniu: escaparea filtrelor, normalizarea numelui, maparea
/// grupurilor pe roluri, validarea configurării și planul de import al
/// structurii. Exact bucățile în care o greșeală nu se vede la demonstrație,
/// dar deschide o ușă.
/// </summary>
public class LdapDirectoryTests
{
    // ── Escaparea filtrelor (RFC 4515) ──────────────────────────────────────

    [Theory]
    [InlineData("*", "\\2a")]
    [InlineData("(", "\\28")]
    [InlineData(")", "\\29")]
    [InlineData("\\", "\\5c")]
    [InlineData("ion.popescu", "ion.popescu")]
    public void Escape_ProtejeazaCaractereleSpeciale(string input, string expected)
    {
        Assert.Equal(expected, LdapFilter.Escape(input));
    }

    [Fact]
    public void Escape_ImpiedicaInjectiaInFiltru()
    {
        // Fără escapare, numele de mai jos ar închide condiția pe sAMAccountName
        // și ar adăuga una proprie: filtrul ar întoarce primul cont din domeniu.
        var injected = "ion)(objectClass=*";
        var escaped  = LdapFilter.Escape(injected);

        Assert.DoesNotContain("(", escaped);
        Assert.DoesNotContain(")", escaped);
        Assert.Contains("\\28", escaped);
        Assert.Contains("\\29", escaped);
    }

    [Fact]
    public void Escape_CodificaDiacriticelePeOcteti()
    {
        // „ș” e pe doi octeți în UTF-8; escaparea per caracter ar produce un
        // filtru pe care serverul îl respinge.
        var escaped = LdapFilter.Escape("ștefan");

        Assert.StartsWith("\\c8\\99", escaped);
        Assert.EndsWith("tefan", escaped);
    }

    // ── Normalizarea numelui ────────────────────────────────────────────────

    [Theory]
    [InlineData("ion.popescu", "ion.popescu")]
    [InlineData("SGDM\\ion.popescu", "ion.popescu")]
    [InlineData("ion.popescu@sgdm.local", "ion.popescu")]
    [InlineData("  SGDM\\ion.popescu  ", "ion.popescu")]
    [InlineData("", "")]
    public void Normalize_ReduceToateFormeleLaAcelasiCont(string input, string expected)
    {
        Assert.Equal(expected, DirectoryUsername.Normalize(input));
    }

    [Fact]
    public void ToBindName_AdaugaSufixulUpnODataSingura()
    {
        Assert.Equal("ion@sgdm.local", DirectoryUsername.ToBindName("ion", "sgdm.local"));
        Assert.Equal("ion@sgdm.local", DirectoryUsername.ToBindName("ion@sgdm.local", "sgdm.local"));
        Assert.Equal("ion", DirectoryUsername.ToBindName("ion", null));
    }

    // ── Maparea grupurilor pe roluri ────────────────────────────────────────

    [Fact]
    public void Parse_CitesteDnUriCuVirgule()
    {
        var mappings = LdapRoleMapper.Parse(
            "CN=SGDM-Admins,OU=Grupuri,DC=sgdm,DC=local=Administrator;SGDM-Sefi=SefDirectie");

        Assert.Equal(2, mappings.Count);
        Assert.Equal("CN=SGDM-Admins,OU=Grupuri,DC=sgdm,DC=local", mappings[0].Group);
        Assert.Equal(UserRole.Administrator, mappings[0].Role);
        Assert.Equal(UserRole.SefDirectie, mappings[1].Role);
    }

    [Theory]
    [InlineData("CN=Grup,DC=x=RolInexistent")]
    [InlineData("fara-rol")]
    public void Parse_RespingeConfigurareaGresita(string specification)
    {
        // Greșeala trebuie să oprească pornirea, nu să producă un refuz de acces
        // inexplicabil peste două zile.
        Assert.Throws<InvalidOperationException>(() => LdapRoleMapper.Parse(specification));
    }

    [Fact]
    public void Resolve_AlegeRolulCelMaiMare()
    {
        var mappings = LdapRoleMapper.Parse("SGDM-Sefi=SefDirectie;SGDM-Admins=Administrator");

        var role = LdapRoleMapper.Resolve(
            new[] { "CN=SGDM-Sefi,OU=Grupuri,DC=sgdm,DC=local", "CN=SGDM-Admins,OU=Grupuri,DC=sgdm,DC=local" },
            mappings, UserRole.Utilizator);

        Assert.Equal(UserRole.Administrator, role);
    }

    [Fact]
    public void Resolve_FaraGrupPotrivit_DaRolulImplicit()
    {
        var mappings = LdapRoleMapper.Parse("SGDM-Admins=Administrator");

        var role = LdapRoleMapper.Resolve(
            new[] { "CN=Domain Users,CN=Users,DC=sgdm,DC=local" }, mappings, UserRole.Utilizator);

        Assert.Equal(UserRole.Utilizator, role);
    }

    [Fact]
    public void Resolve_ToleraSpatiileDinDn()
    {
        var mappings = LdapRoleMapper.Parse("CN=SGDM-Admins,OU=Grupuri,DC=sgdm,DC=local=Administrator");

        var role = LdapRoleMapper.Resolve(
            new[] { "CN=SGDM-Admins, OU=Grupuri, DC=sgdm, DC=local" }, mappings, UserRole.Utilizator);

        Assert.Equal(UserRole.Administrator, role);
    }

    // ── Configurarea ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_DezactivatNuCereNimic()
    {
        new LdapOptions { Enabled = false }.Validate();
    }

    [Fact]
    public void Validate_CereHostSiBaseDn()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new LdapOptions { Enabled = true }.Validate());

        Assert.Contains("Ldap:Host", ex.Message);
        Assert.Contains("Ldap:BaseDn", ex.Message);
    }

    [Fact]
    public void Validate_RefuzaConexiuneaFaraTls()
    {
        // Parolele de domeniu nu au voie să circule în clar: o configurare
        // greșită ar funcționa perfect la test și ar expune toate parolele.
        var options = new LdapOptions
        {
            Enabled = true, Host = "dc.sgdm.local", BaseDn = "DC=sgdm,DC=local",
            UseLdaps = false, UseStartTls = false,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());

        options.AllowInsecurePlaintext = true;
        options.Validate();
    }

    [Fact]
    public void Validate_RefuzaLdapsSiStartTlsImpreuna()
    {
        var options = new LdapOptions
        {
            Enabled = true, Host = "dc.sgdm.local", BaseDn = "DC=sgdm,DC=local",
            UseLdaps = true, UseStartTls = true,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_CereLoculNumeluiInFiltru()
    {
        var options = new LdapOptions
        {
            Enabled = true, Host = "dc.sgdm.local", BaseDn = "DC=sgdm,DC=local",
            UserFilter = "(objectClass=user)",
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void BazeleDeCautare_CadPeBaseDnCandLipsesc()
    {
        var options = new LdapOptions { BaseDn = "DC=sgdm,DC=local" };

        Assert.Equal("DC=sgdm,DC=local", options.EffectiveUserSearchBase);
        Assert.Equal("DC=sgdm,DC=local", options.EffectiveOrgUnitSearchBase);
    }

    // ── Planul de import al structurii ──────────────────────────────────────

    private static DirectoryOrgUnit Ou(string dn, string name, string? parent, int depth) =>
        new(dn, name, parent, depth, null);

    [Fact]
    public void Plan_CreeazaSubdiviziunileNoiCuNivelDupaAdancime()
    {
        var plan = OrgUnitImportPlanner.Plan(
            new[]
            {
                Ou("OU=DTI,DC=sgdm,DC=local", "Direcția Tehnologii Informaționale", null, 0),
                Ou("OU=Retele,OU=DTI,DC=sgdm,DC=local", "Secția Rețele", "OU=DTI,DC=sgdm,DC=local", 1),
            },
            Array.Empty<ExistingOrgUnit>(),
            new[] { 100, 200, 300 });

        Assert.Equal(2, plan.ToCreate);
        Assert.Equal(100, plan.Items[0].Rank);
        Assert.Equal(200, plan.Items[1].Rank);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Plan_LeagaSubdiviziuneaExistentaCuAcelasiNume_InLocSaODubleze()
    {
        var existing = new[]
        {
            new ExistingOrgUnit(Guid.NewGuid(), "Direcția Tehnologii Informaționale", null, null, 100),
        };

        var plan = OrgUnitImportPlanner.Plan(
            new[] { Ou("OU=DTI,DC=sgdm,DC=local", "Direcția Tehnologii Informaționale", null, 0) },
            existing,
            new[] { 100, 200, 300 });

        Assert.Equal(0, plan.ToCreate);
        Assert.Equal(1, plan.ToUpdate);
        Assert.Equal(existing[0].Id, plan.Items[0].ExistingId);
    }

    [Fact]
    public void Plan_ANouaRulare_NuMaiSchimbaNimic()
    {
        var id = Guid.NewGuid();
        var existing = new[]
        {
            new ExistingOrgUnit(id, "Direcția Tehnologii Informaționale", "OU=DTI,DC=sgdm,DC=local", null, 100),
        };

        var plan = OrgUnitImportPlanner.Plan(
            new[] { Ou("OU=DTI,DC=sgdm,DC=local", "Direcția Tehnologii Informaționale", null, 0) },
            existing,
            new[] { 100, 200, 300 });

        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(0, plan.ToCreate);
        Assert.Equal(0, plan.ToUpdate);
    }

    [Fact]
    public void Plan_SemnaleazaSubdiviziunileDisparuteDinAd_FaraSaLeStearga()
    {
        var existing = new[]
        {
            new ExistingOrgUnit(Guid.NewGuid(), "Secția Desființată", "OU=Veche,DC=sgdm,DC=local", null, 200),
        };

        var plan = OrgUnitImportPlanner.Plan(Array.Empty<DirectoryOrgUnit>(), existing, new[] { 100, 200 });

        Assert.Empty(plan.Items);
        Assert.Contains(plan.Warnings, w => w.Contains("Secția Desființată"));
    }

    [Fact]
    public void RankForDepth_ContinuaSubUltimulNivelConfigurat()
    {
        var ranks = new[] { 100, 200, 300 };

        Assert.Equal(100, OrgUnitImportPlanner.RankForDepth(ranks, 0));
        Assert.Equal(300, OrgUnitImportPlanner.RankForDepth(ranks, 2));

        // Un copil trebuie să rămână pe un rang strict mai mare decât părintele
        // (regula din OrgTree.ValidatePlacement), altfel importul ar produce o
        // structură pe care aplicația o refuză la prima editare.
        Assert.Equal(400, OrgUnitImportPlanner.RankForDepth(ranks, 3));
        Assert.Equal(500, OrgUnitImportPlanner.RankForDepth(ranks, 4));
    }

    // ── Reîmpachetarea cheilor după o schimbare de parolă în AD ─────────────

    [Fact]
    public void KeyRewrapRequired_CandParolaDinAdSAuSchimbatDupaImpachetare()
    {
        var user = new User
        {
            AuthProvider           = AuthProvider.Ldap,
            PublicKeyEncryption    = "cheie-publica",
            KeysWrappedAt          = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            DirectoryPasswordSetAt = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc),
        };

        Assert.True(user.KeyRewrapRequired);
    }

    [Fact]
    public void KeyRewrapRequired_EsteFalsPentruConturileLocale()
    {
        var user = new User
        {
            AuthProvider           = AuthProvider.Local,
            PublicKeyEncryption    = "cheie-publica",
            KeysWrappedAt          = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            DirectoryPasswordSetAt = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc),
        };

        Assert.False(user.KeyRewrapRequired);
    }

    [Fact]
    public void KeyRewrapRequired_NuSeDeclanseazaLaReimpachetareaDinAceeasiClipa()
    {
        // Utilizatorul își schimbă parola în AD și intră imediat: fără marja de
        // un minut, rotunjirea FILETIME ar cere reîmpachetarea la nesfârșit.
        var moment = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);

        var user = new User
        {
            AuthProvider           = AuthProvider.Ldap,
            PublicKeyEncryption    = "cheie-publica",
            DirectoryPasswordSetAt = moment,
            KeysWrappedAt          = moment.AddSeconds(5),
        };

        Assert.False(user.KeyRewrapRequired);
    }

    [Fact]
    public void KeyRewrapRequired_EsteFalsFaraChei()
    {
        var user = new User
        {
            AuthProvider           = AuthProvider.Ldap,
            DirectoryPasswordSetAt = DateTime.UtcNow,
            KeysWrappedAt          = DateTime.UtcNow.AddDays(-1),
        };

        Assert.False(user.KeyRewrapRequired);
    }
}
