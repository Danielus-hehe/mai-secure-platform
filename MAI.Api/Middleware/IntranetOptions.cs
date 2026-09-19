using System.Net;

namespace MAI.Api.Middleware
{
    /// <summary>
    /// Configurarea restrictiei de acces la reteaua interna.
    /// Sectiunea "Intranet" din appsettings.json.
    /// </summary>
    public class IntranetOptions
    {
        /// <summary>
        /// Comuta restrictia. Implicit FALSE: activarea ei fara configurare
        /// corecta te blocheaza pe tine primul, iar un API care refuza toata
        /// lumea la deploy e mai rau decat unul deschis in laborator.
        /// Se activeaza explicit, in appsettings.Production.json.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Plajele CIDR carora li se permite accesul. Implicit: spatiul privat
        /// RFC 1918 plus loopback - exact ce inseamna "intranet".
        /// </summary>
        public string[] AllowedNetworks { get; set; } =
        [
            "127.0.0.0/8",      // loopback IPv4
            "::1/128",          // loopback IPv6
            "10.0.0.0/8",       // RFC 1918
            "172.16.0.0/12",    // RFC 1918
            "192.168.0.0/16",   // RFC 1918
        ];

        /// <summary>
        /// Cai exceptate de la restrictie. Se folosesc doar daca ai nevoie de o
        /// sonda de sanatate accesibila din afara (load balancer, monitorizare).
        /// Lasa gol daca nu e cazul: fiecare exceptie e o gaura in perimetru.
        /// </summary>
        public string[] BypassPaths { get; set; } = [];

        /// <summary>
        /// Daca aplicatia sta in spatele unui reverse proxy, IP-ul real vine din
        /// X-Forwarded-For, nu din RemoteIpAddress. Middleware-ul se bazeaza pe
        /// UseForwardedHeaders, care trebuie sa ruleze INAINTEA lui - altfel
        /// filtreaza dupa IP-ul proxy-ului si lasa sa treaca tot internetul.
        /// </summary>
        public bool TrustForwardedHeaders { get; set; } = false;

        /// <summary>
        /// Doar jurnalizeaza cine ar fi fost respins, fara sa respinga. Modul in
        /// care activezi restrictia pe un sistem viu: rulezi o zi asa, verifici
        /// jurnalul, apoi treci pe false.
        /// </summary>
        public bool AuditOnly { get; set; } = false;

        // ── Plajele parsate o singura data, la pornire ───────────────────────

        private List<(IPAddress Network, int PrefixLength)>? _parsed;

        public IReadOnlyList<(IPAddress Network, int PrefixLength)> ParsedNetworks =>
            _parsed ??= Parse();

        private List<(IPAddress, int)> Parse()
        {
            var result = new List<(IPAddress, int)>();

            foreach (var entry in AllowedNetworks ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var parts = entry.Split('/', 2);

                if (!IPAddress.TryParse(parts[0].Trim(), out var address))
                    throw new InvalidOperationException(
                        $"Intranet:AllowedNetworks contine o adresa invalida: '{entry}'.");

                var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;

                var prefix = maxPrefix;
                if (parts.Length == 2 && !int.TryParse(parts[1].Trim(), out prefix))
                    throw new InvalidOperationException(
                        $"Intranet:AllowedNetworks contine un prefix invalid: '{entry}'.");

                if (prefix < 0 || prefix > maxPrefix)
                    throw new InvalidOperationException(
                        $"Intranet:AllowedNetworks: prefixul din '{entry}' este in afara intervalului 0-{maxPrefix}.");

                result.Add((address, prefix));
            }

            if (Enabled && result.Count == 0)
                throw new InvalidOperationException(
                    "Intranet:Enabled=true dar Intranet:AllowedNetworks este gol. " +
                    "Asta ar bloca absolut orice cerere.");

            return result;
        }

        /// <summary>Forteaza parsarea la pornire, ca o configurare gresita sa nu apara abia la prima cerere.</summary>
        public void Validate() => _ = ParsedNetworks;
    }
}