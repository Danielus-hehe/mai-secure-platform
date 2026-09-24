using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using MAI.Api.Controllers;
using MAI.Api.Security;
using MAI.Api.Services;
using MAI.BusinessLogic.Security;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Parola stabilită de administrator trebuie schimbată înainte de orice altă
/// operație, iar regula o aplică serverul, nu doar ecranul din frontend.
///
/// Trei straturi, testate separat: tokenul poartă claim-ul (TokenService),
/// filtrul îl transformă în 403 (decizia pură), iar lista endpointurilor
/// exceptate e exact cea cunoscută (reflecție peste controllere).
/// </summary>
public class PasswordChangeRequiredFilterTests
{
    private static readonly object[] PlainAuthorize = [new AuthorizeAttribute()];

    private static ClaimsPrincipal Authenticated(bool mustChangePassword)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (mustChangePassword)
            claims.Add(new Claim(PasswordChangeRequiredFilter.ClaimType, PasswordChangeRequiredFilter.ClaimValue));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Decizia filtrului
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParolaTemporara_PeEndpointObisnuit_EsteBlocata()
    {
        Assert.True(PasswordChangeRequiredFilter.IsBlocked(Authenticated(true), PlainAuthorize));
    }

    [Fact]
    public void ParolaTemporara_PeEndpointCuRol_EsteBlocata()
    {
        // Contul de administrator creat cu parolă temporară: exact cazul cel
        // mai grav, cu toate drepturile de administrare disponibile.
        object[] adminOnly = [new AuthorizeAttribute { Roles = "Administrator" }];
        Assert.True(PasswordChangeRequiredFilter.IsBlocked(Authenticated(true), adminOnly));
    }

    [Fact]
    public void ParolaTemporara_PeEndpointExceptat_TreceMaiDeparte()
    {
        object[] metadata = [new AuthorizeAttribute(), new AllowDuringPasswordChangeAttribute()];
        Assert.False(PasswordChangeRequiredFilter.IsBlocked(Authenticated(true), metadata));
    }

    [Fact]
    public void ParolaTemporara_PeEndpointAnonim_TreceMaiDeparte()
    {
        object[] metadata = [new AllowAnonymousAttribute()];
        Assert.False(PasswordChangeRequiredFilter.IsBlocked(Authenticated(true), metadata));
    }

    [Fact]
    public void ParolaProprie_NuEsteBlocata()
    {
        Assert.False(PasswordChangeRequiredFilter.IsBlocked(Authenticated(false), PlainAuthorize));
    }

    [Fact]
    public void CerereNeautentificata_NuEsteTratataDeFiltru()
    {
        // Un claim fără identitate autentificată nu înseamnă nimic: respingerea
        // cererilor anonime e treaba middleware-ului de autorizare.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(PasswordChangeRequiredFilter.ClaimType, PasswordChangeRequiredFilter.ClaimValue) }));

        Assert.False(PasswordChangeRequiredFilter.IsBlocked(anonymous, PlainAuthorize));
    }

    [Fact]
    public void ValoareNecunoscutaAClaimului_NuBlocheaza()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(PasswordChangeRequiredFilter.ClaimType, "false") }, "Bearer"));

        Assert.False(PasswordChangeRequiredFilter.IsBlocked(principal, PlainAuthorize));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tokenul
    // ─────────────────────────────────────────────────────────────────────────

    private static TokenService CreateTokenService() => new(
        new JwtOptions
        {
            Key      = "unit-test-jwt-key-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEFG",
            Issuer   = "SGDM",
            Audience = "sgdm-frontend",
        },
        new TwoFactorOptions());

    private static IEnumerable<Claim> ClaimsOf(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims;

    [Fact]
    public void TokenulContuluiCuParolaTemporara_PoartaClaimul()
    {
        var user = new User { Id = Guid.NewGuid(), Username = "temp.user", Role = UserRole.Utilizator, MustChangePassword = true };

        var issued = CreateTokenService().IssueTokens(user);

        Assert.Contains(ClaimsOf(issued.Response.AccessToken), c =>
            c.Type == PasswordChangeRequiredFilter.ClaimType && c.Value == PasswordChangeRequiredFilter.ClaimValue);
    }

    [Fact]
    public void TokenulContuluiCuParolaProprie_NuPoartaClaimul()
    {
        var user = new User { Id = Guid.NewGuid(), Username = "normal.user", Role = UserRole.Utilizator };

        var issued = CreateTokenService().IssueTokens(user);

        Assert.DoesNotContain(ClaimsOf(issued.Response.AccessToken), c =>
            c.Type == PasswordChangeRequiredFilter.ClaimType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lista endpointurilor exceptate
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EndpointurileExceptate_SuntDoarCeleCunoscute()
    {
        // Fiecare excepție e o ușă deschisă pentru cine știe parola temporară.
        // Lista se lărgește doar conștient, într-un commit vizibil la review.
        var asteptate = new HashSet<string>
        {
            // Schimbarea parolei însăși.
            $"{nameof(AuthController)}.{nameof(AuthController.ChangePassword)}",
            // Ecranul citește pachetul de chei ca să știe dacă îl reîmpachetează.
            $"{nameof(KeysController)}.{nameof(KeysController.GetMyBundle)}",
            // Reîmpachetarea după schimbare; cere parola NOUĂ.
            $"{nameof(KeysController)}.{nameof(KeysController.Rewrap)}",
        };

        var exceptate = typeof(AuthController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(c => c.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
                .Where(m => m.GetCustomAttributes<AllowDuringPasswordChangeAttribute>(inherit: true).Any()
                         || c.GetCustomAttributes<AllowDuringPasswordChangeAttribute>(inherit: true).Any())
                .Select(m => $"{c.Name}.{m.Name}"))
            .ToHashSet();

        Assert.True(exceptate.SetEquals(asteptate),
            "Endpointuri exceptate de la schimbarea obligatorie a parolei: " +
            string.Join(", ", exceptate.OrderBy(x => x)) +
            ". Așteptate: " + string.Join(", ", asteptate.OrderBy(x => x)) + ".");
    }
}
