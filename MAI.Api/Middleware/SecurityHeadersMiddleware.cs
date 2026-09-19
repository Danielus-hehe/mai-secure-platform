namespace MAI.Api.Middleware
{
    /// <summary>
    /// Antetele de securitate ale răspunsurilor API.
    ///
    /// API-ul întoarce JSON, nu HTML, deci CSP-ul de aici nu apără o pagină -
    /// apără cazul în care un răspuns ajunge să fie randat direct de browser
    /// (o eroare deschisă într-un tab, un endpoint de export cu Content-Type
    /// ghicit greșit). Politica e cea mai strictă posibilă: nimic nu se încarcă,
    /// nimic nu se randează într-un frame.
    ///
    /// CSP-ul aplicației React este separat și stă în <c>frontend/index.html</c>,
    /// pentru că acolo se decide ce are voie să încarce pagina reală.
    /// </summary>
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

        public Task InvokeAsync(HttpContext context)
        {
            var headers = context.Response.Headers;

            // Un răspuns al API-ului nu are niciodată nevoie să încarce ceva.
            headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            // Fără asta, un răspuns JSON care începe cu ce pare HTML poate fi
            // reinterpretat de browser ca document și executat.
            headers["X-Content-Type-Options"] = "nosniff";

            // Redundant cu frame-ancestors, dar acoperă browserele vechi din
            // parcul instituțional care nu implementează CSP Level 2.
            headers["X-Frame-Options"] = "DENY";

            // URL-urile API conțin identificatori de transfer; nu au ce căuta în
            // antetul Referer al unei cereri către alt domeniu.
            headers["Referrer-Policy"] = "no-referrer";

            // Nimic din API nu are nevoie de cameră, microfon sau geolocație.
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";

            // Un an de HSTS, doar peste HTTPS: trimis pe HTTP e ignorat de browser
            // și doar zgomot în trafic.
            if (context.Request.IsHttps)
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

            return _next(context);
        }
    }

    public static class SecurityHeadersMiddlewareExtensions
    {
        public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
            => app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
