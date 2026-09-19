using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace MAI.Api.Security
{
    /// <summary>Politici de rate limiting. Numele se folosesc în [EnableRateLimiting("...")].</summary>
    public static class RateLimitPolicies
    {
        /// <summary>Login - cea mai strictă. Apără împotriva brute force și credential stuffing.</summary>
        public const string Login = "login";

        /// <summary>Refresh token - mai permisivă, dar tot limitată.</summary>
        public const string Refresh = "refresh";

        /// <summary>Operații de scriere pe parolă (change-password, reset-password).</summary>
        public const string PasswordWrite = "password-write";
    }

    public class RateLimitOptions
    {
        // Login: 10 încercări / 5 minute / IP
        public int LoginPermitLimit { get; set; } = 10;
        public int LoginWindowMinutes { get; set; } = 5;

        // Refresh: 30 / minut / IP (un client legitim face ~4/oră, dar mai multe taburi înmulțesc)
        public int RefreshPermitLimit { get; set; } = 30;
        public int RefreshWindowMinutes { get; set; } = 1;

        // Schimbare parolă: 5 / 15 minute / IP
        public int PasswordWritePermitLimit { get; set; } = 5;
        public int PasswordWriteWindowMinutes { get; set; } = 15;

        /// <summary>
        /// Dacă aplicația rulează în spatele unui reverse proxy (nginx, IIS ARR, Traefik),
        /// pune true și configurează KnownProxies. Altfel RemoteIpAddress este IP-ul
        /// proxy-ului și TOȚI utilizatorii ajung în aceeași găleată de rate limit -
        /// primul care greșește parola îi blochează pe toți.
        /// </summary>
        public bool BehindReverseProxy { get; set; } = false;

        /// <summary>IP-urile proxy-urilor de încredere, ex: ["10.0.0.5"].</summary>
        public string[] KnownProxies { get; set; } = Array.Empty<string>();
    }

    public static class RateLimitingExtensions
    {
        public static IServiceCollection AddMaiRateLimiting(
            this IServiceCollection services, RateLimitOptions options)
        {
            services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                limiter.AddPolicy(RateLimitPolicies.Login, http =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: PartitionKey(http),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit          = options.LoginPermitLimit,
                            Window               = TimeSpan.FromMinutes(options.LoginWindowMinutes),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit           = 0,   // fără coadă: respingem imediat
                            AutoReplenishment    = true,
                        }));

                limiter.AddPolicy(RateLimitPolicies.Refresh, http =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: PartitionKey(http),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit       = options.RefreshPermitLimit,
                            Window            = TimeSpan.FromMinutes(options.RefreshWindowMinutes),
                            QueueLimit        = 0,
                            AutoReplenishment = true,
                        }));

                limiter.AddPolicy(RateLimitPolicies.PasswordWrite, http =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: PartitionKey(http),
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit       = options.PasswordWritePermitLimit,
                            Window            = TimeSpan.FromMinutes(options.PasswordWriteWindowMinutes),
                            QueueLimit        = 0,
                            AutoReplenishment = true,
                        }));

                limiter.OnRejected = async (context, ct) =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RateLimiter");

                    var ip   = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var path = context.HttpContext.Request.Path;

                    logger.LogWarning("Rate limit depasit: IP={Ip} Path={Path}", ip, path);

                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                        ? (int)value.TotalSeconds
                        : 60;

                    context.HttpContext.Response.Headers.RetryAfter =
                        retryAfter.ToString(CultureInfo.InvariantCulture);
                    context.HttpContext.Response.StatusCode  = StatusCodes.Status429TooManyRequests;
                    context.HttpContext.Response.ContentType = "application/json";

                    await context.HttpContext.Response.WriteAsync(
                        $"{{\"message\":\"Prea multe cereri. Reincercati peste {retryAfter} secunde.\"," +
                        $"\"retryAfter\":{retryAfter}}}",
                        ct);
                };
            });

            return services;
        }

        /// <summary>
        /// Cheia de partiționare. IPv4 se ia întreg; la IPv6 se ia prefixul /64, pentru că
        /// un singur abonat primește de obicei un bloc /64 întreg și ar putea roti adresele
        /// ca să ocolească limita.
        /// </summary>
        private static string PartitionKey(HttpContext http)
        {
            var ip = http.Connection.RemoteIpAddress;
            if (ip is null) return "unknown";

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                Array.Clear(bytes, 8, 8);            // păstrăm doar primii 64 de biți
                return new IPAddress(bytes).ToString() + "/64";
            }

            return ip.ToString();
        }
    }
}