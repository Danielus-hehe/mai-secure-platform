using System.Net;
using System.Net.Sockets;

namespace MAI.Api.Middleware
{
    /// <summary>
    /// Restrictioneaza accesul la plajele de adrese ale retelei interne.
    ///
    /// Versiunea anterioara a acestei clase avea corpul gol si nu era inregistrata
    /// in Program.cs. Intr-o lucrare pe tema securitatii informationale, o clasa
    /// numita "IntranetOnly" care nu restrictioneaza nimic este mai rea decat
    /// absenta ei: sugereaza un control care nu exista.
    ///
    /// Ce face si ce NU face:
    ///
    ///   FACE — refuza cererile venite din afara plajelor configurate, inainte de
    ///   orice autentificare. Este un perimetru, nu o autorizare: reduce suprafata
    ///   expusa, dar nu inlocuieste JWT-ul si RBAC-ul de dedesubt.
    ///
    ///   NU FACE — nu opreste un atacator care se afla DEJA in reteaua interna.
    ///   Filtrarea dupa IP este un control de perimetru; adresele sursa se pot
    ///   falsifica intr-o retea nesegmentata, iar un dispozitiv compromis din
    ///   intranet trece nestingherit. De aceea ramane un strat in plus, nu
    ///   singurul strat.
    ///
    /// Se pune INAINTEA rate limiting-ului si a autentificarii: o cerere din afara
    /// perimetrului nu merita nici un ciclu de Argon2, nici un slot de rate limit.
    /// </summary>
    public class IntranetOnlyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IntranetOptions _options;
        private readonly ILogger<IntranetOnlyMiddleware> _logger;

        public IntranetOnlyMiddleware(
            RequestDelegate next,
            IntranetOptions options,
            ILogger<IntranetOnlyMiddleware> logger)
        {
            _next    = next;
            _options = options;
            _logger  = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.Enabled)
            {
                await _next(context);
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;

            foreach (var bypass in _options.BypassPaths)
            {
                if (!string.IsNullOrWhiteSpace(bypass) &&
                    path.StartsWith(bypass, StringComparison.OrdinalIgnoreCase))
                {
                    await _next(context);
                    return;
                }
            }

            var remoteIp = context.Connection.RemoteIpAddress;

            if (remoteIp is null)
            {
                // Fara adresa nu putem decide. Refuzam: intr-un control de
                // perimetru, necunoscutul se trateaza ca exterior, nu ca interior.
                _logger.LogWarning("Cerere fara adresa sursa catre {Path}. Respinsa.", path);
                await DenyAsync(context);
                return;
            }

            // ::ffff:10.0.0.5 este 10.0.0.5 scris in forma IPv6. Fara normalizare,
            // comparatia cu plaja 10.0.0.0/8 esueaza si blocam trafic legitim.
            if (remoteIp.IsIPv4MappedToIPv6)
                remoteIp = remoteIp.MapToIPv4();

            if (IsAllowed(remoteIp))
            {
                await _next(context);
                return;
            }

            if (_options.AuditOnly)
            {
                _logger.LogWarning(
                    "[AUDIT] Cerere din afara intranetului: {Ip} → {Method} {Path}. Permisa (AuditOnly=true).",
                    remoteIp, context.Request.Method, path);

                await _next(context);
                return;
            }

            _logger.LogWarning(
                "Cerere respinsa din afara intranetului: {Ip} → {Method} {Path}",
                remoteIp, context.Request.Method, path);

            await DenyAsync(context);
        }

        /// <summary>
        /// Raspunsul nu spune de ce a fost refuzata cererea si nu enumera plajele
        /// permise. Cine e in afara perimetrului nu are de ce sa afle topologia
        /// retelei interne dintr-un mesaj de eroare.
        /// </summary>
        private static async Task DenyAsync(HttpContext context)
        {
            context.Response.StatusCode  = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";

            await context.Response.WriteAsync(
                "{\"message\":\"Acces permis exclusiv din reteaua interna a institutiei.\"}");
        }

        private bool IsAllowed(IPAddress address)
        {
            foreach (var (network, prefix) in _options.ParsedNetworks)
            {
                if (IsInSubnet(address, network, prefix))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Comparatie pe biti intre adresa si prefixul retelei.
        ///
        /// Se face pe octetii bruti, nu pe reprezentarea text: compararea de
        /// siruri ("192.168." ca prefix) ar lasa sa treaca 192.1680.x si ar
        /// respinge adrese valide scrise altfel.
        /// </summary>
        private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
        {
            if (address.AddressFamily != network.AddressFamily)
                return false;

            var addressBytes = address.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();

            if (addressBytes.Length != networkBytes.Length)
                return false;

            var fullBytes    = prefixLength / 8;
            var partialBits  = prefixLength % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (addressBytes[i] != networkBytes[i])
                    return false;
            }

            if (partialBits == 0)
                return true;

            var mask = (byte)(0xFF << (8 - partialBits));
            return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
        }
    }

    public static class IntranetOnlyMiddlewareExtensions
    {
        /// <summary>
        /// Inregistreaza restrictia. Apeleaza-o DUPA UseForwardedHeaders (daca
        /// esti in spatele unui proxy) si INAINTEA lui UseRateLimiter.
        /// </summary>
        public static IApplicationBuilder UseIntranetOnly(this IApplicationBuilder app)
            => app.UseMiddleware<IntranetOnlyMiddleware>();
    }
}