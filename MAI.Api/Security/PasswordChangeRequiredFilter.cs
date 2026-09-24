using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MAI.Api.Security
{
    /// <summary>
    /// Aplică pe server regula „parola stabilită de administrator se schimbă
    /// înainte de orice altceva”.
    ///
    /// Până acum regula exista în două locuri: ecranul PasswordChangeGate din
    /// frontend și refuzul de a înregistra chei E2EE din KeysController. Restul
    /// API-ului accepta tokenul obținut cu parola temporară. Cine avea parola
    /// temporară - utilizatorul, dar și administratorul care a stabilit-o sau
    /// oricine a văzut-o pe hârtie ori în chat - putea, ocolind interfața, să
    /// citească documentele interne ale contului, lista de transferuri sau, pentru
    /// un cont de administrator creat așa, să folosească toate drepturile lui.
    /// Interfața nu e granița de securitate; API-ul este.
    ///
    /// Mecanismul: TokenService pune claim-ul <see cref="ClaimType"/> în JWT cât
    /// timp User.MustChangePassword este true. Filtrul răspunde 403 cu codul
    /// <see cref="ErrorCode"/> pe orice endpoint autentificat, cu excepția celor
    /// marcate <see cref="AllowDuringPasswordChangeAttribute"/>. Claim-ul, nu o
    /// interogare în baza de date: filtrul rulează la fiecare cerere, iar după
    /// schimbarea parolei toate sesiunile se închid, deci următorul token vine
    /// oricum fără claim.
    ///
    /// Ordinea în pipeline: rulează după middleware-ul de autentificare și
    /// autorizare pe rol, înaintea filtrului 2FA (vezi Program.cs). Un cont cu
    /// parolă temporară află întâi că trebuie să o schimbe, nu că îi lipsește 2FA.
    /// </summary>
    public sealed class PasswordChangeRequiredFilter : IAuthorizationFilter
    {
        /// <summary>Codul din corpul răspunsului 403, citit de frontend.</summary>
        public const string ErrorCode = "PASSWORD_CHANGE_REQUIRED";

        /// <summary>Claim-ul emis de TokenService pentru contul cu parolă temporară.</summary>
        public const string ClaimType = "pwd_change";

        /// <summary>Singura valoare a claim-ului care declanșează blocarea.</summary>
        public const string ClaimValue = "required";

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (!IsBlocked(context.HttpContext.User, context.ActionDescriptor.EndpointMetadata))
                return;

            context.Result = new ObjectResult(new
            {
                code    = ErrorCode,
                message = "Parola contului a fost stabilită de administrator. " +
                          "Alegeți o parolă proprie înainte de a continua.",
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }

        /// <summary>
        /// Decizia, separată de contextul MVC ca să poată fi testată direct
        /// (la fel ca PrivilegedMfaFilter.RequiresSecondFactor).
        /// </summary>
        public static bool IsBlocked(ClaimsPrincipal user, IEnumerable<object> endpointMetadata)
        {
            if (user.Identity?.IsAuthenticated != true) return false;
            if (!RequiresPasswordChange(user)) return false;

            var metadata = endpointMetadata as IList<object> ?? endpointMetadata.ToList();

            // Endpointurile anonime (login, refresh, logout, activare, resetare)
            // nu depind de identitatea din token, deci nu au ce bloca.
            if (metadata.OfType<IAllowAnonymous>().Any()) return false;

            return !metadata.OfType<AllowDuringPasswordChangeAttribute>().Any();
        }

        /// <summary>True dacă tokenul a fost emis pentru un cont cu parolă temporară.</summary>
        public static bool RequiresPasswordChange(ClaimsPrincipal user) =>
            user.HasClaim(ClaimType, ClaimValue);
    }

    /// <summary>
    /// Marchează endpointurile de care are nevoie ecranul de schimbare a parolei
    /// și care rămân deci accesibile cu parola temporară.
    ///
    /// Lista e intenționat minimă și verificată de PasswordChangeRequiredFilterTests:
    /// un endpoint nou marcat aici trebuie adăugat conștient și în test.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class AllowDuringPasswordChangeAttribute : Attribute
    {
    }
}
