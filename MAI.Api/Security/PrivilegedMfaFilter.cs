using System.Security.Claims;
using MAI.BusinessLogic.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MAI.Api.Security
{
    /// <summary>
    /// Aplică <c>TwoFactor:RequiredForPrivilegedRoles</c>: când opțiunea e activă,
    /// endpointurile care cer explicit un rol (administrare, audit, alerte,
    /// publicarea documentelor) acceptă doar tokenuri obținute cu al doilea factor.
    ///
    /// De ce la nivel de endpoint și nu la login:
    ///   • Un login refuzat ar bloca exact contul care trebuie să-și activeze 2FA:
    ///     înrolarea se face din profil, deci după autentificare.
    ///   • Filtrul protejează ce contează: operațiile privilegiate. Un Șef de
    ///     direcție fără 2FA poate în continuare să-și trimită și primească
    ///     fișierele, dar nu poate citi jurnalul de audit până nu activează 2FA.
    ///
    /// Ordinea în pipeline: middleware-ul de autorizare verifică rolul înaintea
    /// filtrelor MVC. Filtrul rulează deci doar pentru cine are deja rolul cerut,
    /// iar un 403 de aici înseamnă strict „rol corect, dar fără al doilea factor”.
    /// </summary>
    public sealed class PrivilegedMfaFilter : IAuthorizationFilter
    {
        /// <summary>Codul din corpul răspunsului 403, citit de frontend.</summary>
        public const string ErrorCode = "MFA_REQUIRED";

        /// <summary>
        /// Tipul în care JwtBearer transformă claim-ul „amr” la validare
        /// (MapInboundClaims e activ implicit). Se acceptă ambele forme, ca
        /// verificarea să nu depindă de o setare a handler-ului de tokenuri.
        /// </summary>
        private const string MappedAmrClaimType =
            "http://schemas.microsoft.com/claims/authnmethodsreferences";

        private readonly TwoFactorOptions _options;

        public PrivilegedMfaFilter(TwoFactorOptions options) => _options = options;

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (!RequiresSecondFactor(
                    _options.RequiredForPrivilegedRoles,
                    context.HttpContext.User,
                    context.ActionDescriptor.EndpointMetadata))
            {
                return;
            }

            context.Result = new ObjectResult(new
            {
                code    = ErrorCode,
                message = "Această operație cere autentificare în doi pași pentru rolul dumneavoastră. " +
                          "Activați 2FA din pagina Profil, apoi autentificați-vă din nou.",
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }

        /// <summary>
        /// Decizia, separată de contextul MVC ca să poată fi testată direct.
        /// </summary>
        /// <param name="enabled">Valoarea opțiunii din configurare.</param>
        /// <param name="user">Identitatea din tokenul deja validat.</param>
        /// <param name="endpointMetadata">Atributele endpointului (clasă + metodă).</param>
        public static bool RequiresSecondFactor(
            bool enabled, ClaimsPrincipal user, IEnumerable<object> endpointMetadata)
        {
            if (!enabled) return false;
            if (user.Identity?.IsAuthenticated != true) return false;

            var metadata = endpointMetadata as IList<object> ?? endpointMetadata.ToList();

            if (metadata.OfType<IAllowAnonymous>().Any()) return false;

            // Doar endpointurile care restrâng accesul la un rol. [Authorize] fără
            // roluri (transferuri, profil, înrolarea 2FA) rămâne accesibil.
            var roleRestricted = metadata
                .OfType<IAuthorizeData>()
                .Any(a => !string.IsNullOrWhiteSpace(a.Roles));

            return roleRestricted && !HasSecondFactor(user);
        }

        /// <summary>True dacă tokenul a fost emis după pasul 2FA (amr = mfa, RFC 8176).</summary>
        public static bool HasSecondFactor(ClaimsPrincipal user) =>
            user.Claims.Any(c =>
                (c.Type == "amr" || c.Type == MappedAmrClaimType) &&
                string.Equals(c.Value, "mfa", StringComparison.Ordinal));
    }
}
