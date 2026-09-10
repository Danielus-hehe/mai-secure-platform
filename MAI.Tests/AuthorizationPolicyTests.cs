using System.Reflection;
using MAI.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace MAI.Tests;

/// <summary>
/// Teste de arhitectură pentru autorizare: citesc atributele prin reflecție, fără
/// server, fără bază de date, în milisecunde.
///
/// Motivul existenței lor: până în 2026-09-10, crearea de conturi, schimbarea
/// rolului și (de)activarea din UsersController aveau doar [Authorize] pe clasă.
/// Orice Utilizator își putea acorda singur rolul de Administrator. Pagina
/// /users era ascunsă în meniu, deci nimeni nu a observat — interfața nu e
/// granița de securitate, API-ul este. Testele de mai jos fac din regula asta
/// ceva ce CI-ul verifică la fiecare push, nu ceva ce trebuie ținut minte.
///
/// Ce NU verifică: logica din interiorul metodelor (ex. „doar expeditorul poate
/// retrage”). Aceea ține de teste de integrare, cu bază de date.
/// </summary>
public class AuthorizationPolicyTests
{
    private const string Admin = "Administrator";
    private const string Supervisor = "SefDirectie";
    private const string RegularUser = "Utilizator";

    private static readonly Assembly ApiAssembly = typeof(UsersController).Assembly;

    // ─────────────────────────────────────────────────────────────────────────
    // Utilitare
    // ─────────────────────────────────────────────────────────────────────────

    private static IEnumerable<Type> Controllers() =>
        ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t));

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());

    private static MethodInfo Action(Type controller, string name) =>
        Actions(controller).Single(m => m.Name == name);

    private static bool IsAnonymous(Type controller, MethodInfo action) =>
        action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()
        || controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    /// <summary>
    /// Toate atributele [Authorize] care se aplică acțiunii (clasă + metodă).
    /// ASP.NET le cumulează: fiecare trebuie satisfăcut. În interiorul unui
    /// singur atribut, rolurile separate prin virgulă sunt alternative.
    /// </summary>
    private static List<AuthorizeAttribute> Effective(Type controller, MethodInfo action) =>
        controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToList();

    private static string[] Roles(AuthorizeAttribute attribute) =>
        (attribute.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Adevărat dacă cel puțin un atribut aplicat restrânge accesul la un subset
    /// din <paramref name="allowed"/>. Pentru că atributele se cumulează, unul
    /// singur suficient de strict ajunge.
    /// </summary>
    private static bool RestrictedTo(Type controller, MethodInfo action, params string[] allowed) =>
        Effective(controller, action).Any(a =>
        {
            var roles = Roles(a);
            return roles.Length > 0 && roles.All(allowed.Contains);
        });

    // ─────────────────────────────────────────────────────────────────────────
    // Regula generală
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FiecareEndpoint_AreODecizieExplicitaDeAutorizare()
    {
        // Un controller nou fără [Authorize] ar fi public pe tăcute: ASP.NET nu
        // are o politică implicită de respingere în Program.cs. Fiecare acțiune
        // trebuie să spună ori cine are voie, ori că e intenționat anonimă.
        var nedecise = Controllers()
            .SelectMany(c => Actions(c).Select(a => (c, a)))
            .Where(x => !IsAnonymous(x.c, x.a) && Effective(x.c, x.a).Count == 0)
            .Select(x => $"{x.c.Name}.{x.a.Name}")
            .ToList();

        Assert.True(nedecise.Count == 0,
            "Endpointuri fără [Authorize] și fără [AllowAnonymous]: " + string.Join(", ", nedecise));
    }

    [Fact]
    public void EndpointurileAnonime_SuntDoarCeleCunoscute()
    {
        // Lista e scurtă intenționat. Un endpoint anonim nou trebuie adăugat aici
        // conștient, într-un commit care se vede la review.
        var asteptate = new HashSet<string>
        {
            "AuthController.Login",
            "AuthController.VerifyTwoFactor",
            "AuthController.Refresh",
            "AuthController.Logout",
            "HealthController.Get",
            "HealthController.Live",
        };

        var anonime = Controllers()
            .SelectMany(c => Actions(c).Where(a => IsAnonymous(c, a)).Select(a => $"{c.Name}.{a.Name}"))
            .ToHashSet();

        var neasteptate = anonime.Except(asteptate).ToList();

        Assert.True(neasteptate.Count == 0,
            "Endpointuri anonime neașteptate: " + string.Join(", ", neasteptate) +
            ". Dacă sunt intenționate, adăugați-le în lista din test.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Administrarea conturilor
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(UsersController.CreateUser))]
    [InlineData(nameof(UsersController.ResetPassword))]
    [InlineData(nameof(UsersController.Unlock))]
    [InlineData(nameof(UsersController.ChangeRole))]
    [InlineData(nameof(UsersController.Deactivate))]
    [InlineData(nameof(UsersController.Activate))]
    public void UsersController_OperatiileDeAdministrare_CerAdministrator(string action)
    {
        var type = typeof(UsersController);
        Assert.True(RestrictedTo(type, Action(type, action), Admin),
            $"UsersController.{action} trebuie restricționat la rolul {Admin}.");
    }

    [Fact]
    public void UsersController_ListaCompletaDeConturi_EsteDoarPentruRoluriPrivilegiate()
    {
        // Email, rol, stare de blocare, ultima autentificare: harta de care are
        // nevoie cineva care pregătește phishing intern. Șeful de direcție o
        // poate citi, utilizatorul obișnuit nu.
        var type = typeof(UsersController);
        Assert.True(RestrictedTo(type, Action(type, nameof(UsersController.GetAll)), Admin, Supervisor));
    }

    [Fact]
    public void UsersController_ListaDeDestinatari_RamaneAccesibilaOricuiAutentificat()
    {
        // Controlul invers: dacă cineva mută [Authorize(Roles)] pe clasă ca să
        // „simplifice”, dropdown-ul de destinatari se închide pentru toată lumea.
        var type   = typeof(UsersController);
        var action = Action(type, nameof(UsersController.GetAllForDropdown));

        Assert.False(IsAnonymous(type, action));
        Assert.All(Effective(type, action), a => Assert.True(string.IsNullOrEmpty(a.Roles)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Jurnalul de audit și alte endpointuri privilegiate
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(AuditLogsController.GetAll))]
    [InlineData(nameof(AuditLogsController.Export))]
    [InlineData(nameof(AuditLogsController.GetUsernames))]
    public void AuditLogsController_EsteInaccesibilUtilizatorilorObisnuiti(string action)
    {
        var type = typeof(AuditLogsController);
        Assert.True(RestrictedTo(type, Action(type, action), Admin, Supervisor),
            $"AuditLogsController.{action} nu trebuie să fie accesibil rolului {RegularUser}.");
    }

    [Theory]
    [InlineData(typeof(TwoFactorController), nameof(TwoFactorController.AdminReset))]
    [InlineData(typeof(StatsController),     nameof(StatsController.GetAdminStats))]
    [InlineData(typeof(StatsController),     nameof(StatsController.RunExpiration))]
    public void EndpointurileAdministrative_CerAdministrator(Type controller, string action)
    {
        Assert.True(RestrictedTo(controller, Action(controller, action), Admin),
            $"{controller.Name}.{action} trebuie restricționat la rolul {Admin}.");
    }

    [Fact]
    public void AlerteleDeSecuritate_SuntPentruSupervizori()
    {
        var type = typeof(StatsController);
        Assert.True(RestrictedTo(type, Action(type, nameof(StatsController.GetAlerts)), Admin, Supervisor));
    }
}
