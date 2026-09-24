using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Implementarea peste System.DirectoryServices.Protocols (LDAP v3).
    ///
    /// De ce protocolul brut și nu System.DirectoryServices (ADSI):
    /// ADSI există doar pe Windows, iar API-ul rulează în container Linux.
    /// SDP merge pe amândouă și vorbește direct LDAPS cu orice server, inclusiv
    /// cu Samba AD din docker-compose.
    ///
    /// Biblioteca e sincronă. Apelurile stau în Task.Run ca firul cererii HTTP
    /// să nu fie blocat pe rețea; fiecare operație își deschide și își închide
    /// propria conexiune, deci nu există stare partajată între cereri.
    /// </summary>
    public sealed class LdapDirectoryService : IDirectoryService
    {
        private readonly LdapOptions _options;
        private readonly ILogger<LdapDirectoryService> _logger;
        private readonly IReadOnlyList<GroupRoleMapping> _mappings;

        /// <summary>
        /// Regula de potrivire în lanț a AD-ului (LDAP_MATCHING_RULE_IN_CHAIN).
        /// Cu ea, o singură căutare întoarce și grupurile din care contul face
        /// parte indirect, prin alte grupuri.
        /// </summary>
        private const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

        /// <summary>Atributele citite pentru un cont. Cerute explicit: altfel AD le trimite pe toate.</summary>
        private static readonly string[] UserAttributes =
        {
            "objectGUID", "sAMAccountName", "distinguishedName", "displayName", "cn",
            "mail", "userPrincipalName", "memberOf", "userAccountControl", "pwdLastSet",
        };

        /// <summary>
        /// Ultimul certificat prezentat de server, păstrat pentru testul de
        /// conexiune. Nu e folosit la nicio decizie de securitate: validarea se
        /// face în callback, în momentul conexiunii.
        /// </summary>
        // Doar subiectul și amprenta, nu obiectul certificatului. Certificatul
        // primit în callbackul TLS aparține lui SslStream, care îl eliberează la
        // închiderea conexiunii; o referință păstrată la el arunca apoi
        // CryptographicException la citirea lui .Subject. Asta se întâmpla chiar
        // în blocul catch al testului de conexiune, deci excepția ieșea din metodă
        // și pagina primea 500 în loc de mesajul de eroare.
        private ServerCertificateInfo? _lastServerCertificate;

        public LdapDirectoryService(LdapOptions options, ILogger<LdapDirectoryService> logger)
        {
            _options  = options;
            _logger   = logger;
            _mappings = LdapRoleMapper.Parse(options.GroupRoleMappings);
        }

        public bool Enabled => _options.Enabled;

        /// <summary>Maparea grupurilor pe roluri, interpretată o singură dată.</summary>
        public IReadOnlyList<GroupRoleMapping> RoleMappings => _mappings;

        // ═════════════════════════════════════════════════════════════════════
        // Autentificare
        // ═════════════════════════════════════════════════════════════════════

        public Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct)
        {
            var account = DirectoryUsername.Normalize(username);

            if (account.Length == 0 || string.IsNullOrEmpty(password))
                return Task.FromResult(DirectoryAuthResult.Fail(DirectoryAuthStatus.InvalidCredentials, "Credentiale lipsa"));

            return Task.Run(() => Authenticate(account, password), ct);
        }

        private DirectoryAuthResult Authenticate(string account, string password)
        {
            try
            {
                // Varianta cu cont de serviciu: întâi aflăm DN-ul real al
                // contului, apoi facem bind cu el. E singura care funcționează
                // când domeniul nu are UPN configurat pentru toți utilizatorii.
                if (_options.HasServiceAccount)
                {
                    using var service = Connect();
                    Bind(service, _options.BindDn, _options.BindPassword);

                    var entry = FindEntry(service, account);
                    if (entry is null)
                        return DirectoryAuthResult.Fail(DirectoryAuthStatus.UserNotFound, $"Contul {account} nu exista in AD");

                    var dn = entry.DistinguishedName;

                    try
                    {
                        using var user = Connect();
                        Bind(user, dn, password);
                    }
                    catch (LdapException ex) when (ex.ErrorCode == (int)LdapError.InvalidCredentials)
                    {
                        return Classify(ex);
                    }

                    return DirectoryAuthResult.Ok(ReadUser(service, entry));
                }

                // Fără cont de serviciu: bind direct cu UPN-ul utilizatorului,
                // apoi căutăm cu aceeași conexiune. Merge doar dacă domeniul are
                // un sufix UPN configurat - altfel nu avem ce trimite la bind.
                if (string.IsNullOrWhiteSpace(_options.Realm))
                {
                    return DirectoryAuthResult.Fail(DirectoryAuthStatus.ServiceAccountRejected,
                        "Fara cont de serviciu si fara LDAP_REALM nu se poate face bind");
                }

                var bindName = DirectoryUsername.ToBindName(account, _options.Realm);

                using var connection = Connect();
                try
                {
                    Bind(connection, bindName, password);
                }
                catch (LdapException ex)
                {
                    return Classify(ex);
                }

                var own = FindEntry(connection, account);
                if (own is null)
                {
                    return DirectoryAuthResult.Fail(DirectoryAuthStatus.UserNotFound,
                        $"Bind reusit pentru {account}, dar contul nu se vede sub baza de cautare");
                }

                return DirectoryAuthResult.Ok(ReadUser(connection, own));
            }
            catch (LdapException ex)
            {
                _logger.LogWarning(ex, "LDAP: eroare la autentificarea contului {Account} ({Endpoint})",
                    account, _options.Endpoint);
                return Classify(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LDAP: {Endpoint} inaccesibil", _options.Endpoint);
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.ServerUnavailable, Shorten(ex.Message));
            }
        }

        public Task<bool> VerifyPasswordAsync(string bindName, string password, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(bindName) || string.IsNullOrEmpty(password))
                return Task.FromResult(false);

            return Task.Run(() =>
            {
                try
                {
                    using var connection = Connect();
                    Bind(connection, bindName, password);
                    return true;
                }
                catch (LdapException ex)
                {
                    // Un bind respins e un răspuns valid („parola nu e bună”), nu
                    // o defecțiune. Doar problemele de rețea se loghează ca eroare.
                    if (ex.ErrorCode != (int)LdapError.InvalidCredentials)
                        _logger.LogWarning(ex, "LDAP: verificarea parolei pentru {BindName} a esuat", bindName);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LDAP: verificarea parolei pentru {BindName} a esuat", bindName);
                    return false;
                }
            }, ct);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Căutări
        // ═════════════════════════════════════════════════════════════════════

        public Task<DirectoryUser?> FindUserAsync(string username, CancellationToken ct)
        {
            var account = DirectoryUsername.Normalize(username);
            if (account.Length == 0) return Task.FromResult<DirectoryUser?>(null);

            return Task.Run<DirectoryUser?>(() =>
            {
                using var connection = Connect();
                Bind(connection, _options.BindDn, _options.BindPassword);

                var entry = FindEntry(connection, account);
                return entry is null ? null : ReadUser(connection, entry);
            }, ct);
        }

        public Task<IReadOnlyList<DirectoryOrgUnit>> GetOrganizationalUnitsAsync(CancellationToken ct) =>
            Task.Run<IReadOnlyList<DirectoryOrgUnit>>(() =>
            {
                using var connection = Connect();
                Bind(connection, _options.BindDn, _options.BindPassword);

                var baseDn  = _options.EffectiveOrgUnitSearchBase;
                var request = new SearchRequest(
                    baseDn,
                    "(objectClass=organizationalUnit)",
                    SearchScope.Subtree,
                    "distinguishedName", "ou", "name", "description");

                var response = (SearchResponse)connection.SendRequest(request, Timeout);

                var all = new List<(string Dn, string Name, string? Description)>();

                foreach (SearchResultEntry entry in response.Entries)
                {
                    var dn   = entry.DistinguishedName;
                    var name = First(entry, "ou") ?? First(entry, "name") ?? NameFromDn(dn);
                    if (string.IsNullOrWhiteSpace(dn) || string.IsNullOrWhiteSpace(name)) continue;

                    all.Add((dn, name.Trim(), First(entry, "description")));
                }

                // AD nu spune cine e părintele cui: ierarhia e în DN. Un OU e
                // copilul altuia dacă DN-ul lui se termină cu DN-ul celuilalt.
                var dnSet = new HashSet<string>(all.Select(a => a.Dn), StringComparer.OrdinalIgnoreCase);

                var result = new List<DirectoryOrgUnit>();
                foreach (var (dn, name, description) in all)
                {
                    var parentDn = ParentDn(dn);
                    var parent   = parentDn is not null && dnSet.Contains(parentDn) ? parentDn : null;

                    result.Add(new DirectoryOrgUnit(dn, name, parent, DepthOf(dn, dnSet), description));
                }

                return result
                    .OrderBy(u => u.Depth)
                    .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, ct);

        public Task<DirectoryProbe> TestConnectionAsync(CancellationToken ct) =>
            Task.Run(() =>
            {
                try
                {
                    CaptureServerCertificate();

                    using var connection = Connect();

                    if (!_options.HasServiceAccount)
                    {
                        // Fără cont de serviciu tot verificăm că portul răspunde și
                        // că certificatul e acceptat: bind anonim, care în AD e de
                        // obicei refuzat, dar abia DUPĂ negocierea TLS.
                        try { connection.Bind(new NetworkCredential(string.Empty, string.Empty)); }
                        catch (LdapException) { /* refuzul e așteptat */ }

                        return new DirectoryProbe(
                            Reachable: true, ServiceAccountBound: false, UserCount: 0, OrgUnitCount: 0,
                            _lastServerCertificate?.Subject, _lastServerCertificate?.Thumbprint,
                            "Nu e configurat un cont de serviciu (LDAP_BIND_DN), deci nu se pot număra obiectele.");
                    }

                    Bind(connection, _options.BindDn, _options.BindPassword);

                    var users = (SearchResponse)connection.SendRequest(new SearchRequest(
                        _options.EffectiveUserSearchBase,
                        "(&(objectClass=user)(objectCategory=person))",
                        SearchScope.Subtree,
                        "distinguishedName"), Timeout);

                    var ous = (SearchResponse)connection.SendRequest(new SearchRequest(
                        _options.EffectiveOrgUnitSearchBase,
                        "(objectClass=organizationalUnit)",
                        SearchScope.Subtree,
                        "distinguishedName"), Timeout);

                    return new DirectoryProbe(
                        Reachable: true,
                        ServiceAccountBound: true,
                        UserCount: users.Entries.Count,
                        OrgUnitCount: ous.Entries.Count,
                        _lastServerCertificate?.Subject,
                        _lastServerCertificate?.Thumbprint,
                        Error: null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LDAP: testul de conexiune catre {Endpoint} a esuat", _options.Endpoint);
                    return new DirectoryProbe(false, false, 0, 0,
                        _lastServerCertificate?.Subject, _lastServerCertificate?.Thumbprint, Shorten(ex.Message));
                }
            }, ct);

        // ═════════════════════════════════════════════════════════════════════
        // Conexiune și TLS
        // ═════════════════════════════════════════════════════════════════════

        private TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

        private LdapConnection Connect()
        {
            var identifier = new LdapDirectoryIdentifier(
                _options.Host, _options.Port, fullyQualifiedDnsHostName: true, connectionless: false);

            var connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Basic,   // simplu peste TLS: merge identic pe Linux și pe Windows
                Timeout  = Timeout,
            };

            connection.SessionOptions.ProtocolVersion = 3;

            // Urmarea referralurilor e dezactivată: un DC compromis sau greșit
            // configurat ar putea trimite clientul (cu tot cu parolă) spre alt
            // server, ales de el.
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

            // Validarea certificatului diferă între platforme, pentru că
            // biblioteca LDAP de dedesubt diferă:
            //   • Windows (wldap32) acceptă un callback .NET - îl folosim pentru
            //     amprentă, CA propriu și verificarea numelui;
            //   • Linux (OpenLDAP, adică și containerul Docker) NU suportă
            //     callbackul: setarea lui aruncă PlatformNotSupportedException.
            //     Acolo validează libldap însuși, după LDAPTLS_CACERT și
            //     LDAPTLS_REQCERT din mediu (puse de docker-compose), inclusiv
            //     potrivirea numelui din certificat cu LDAP_HOST.
            if ((_options.UseLdaps || _options.UseStartTls) && OperatingSystem.IsWindows())
                connection.SessionOptions.VerifyServerCertificate = (_, certificate) => Validate(certificate);

            if (_options.UseLdaps)
                connection.SessionOptions.SecureSocketLayer = true;

            if (_options.UseStartTls)
            {
                // StartTLS ÎNAINTE de bind: altfel parola pleacă în clar pe
                // conexiunea încă necriptată, iar criptarea de după nu mai ajută.
                connection.SessionOptions.StartTransportLayerSecurity(null);
            }

            return connection;
        }

        /// <summary>
        /// Citește certificatul prezentat de server printr-o conexiune TLS
        /// separată, DOAR pentru afișare în pagina de administrare (subiect și
        /// amprentă, de copiat în configurare). Nu ia nicio decizie de
        /// securitate: pe Linux validarea reală o face libldap, pe Windows
        /// callbackul din <see cref="Validate"/>.
        /// </summary>
        private void CaptureServerCertificate()
        {
            if (!_options.UseLdaps) return;   // la StartTLS, TLS-ul începe abia după o cerere LDAP

            try
            {
                using var tcp = new TcpClient();
                if (!tcp.ConnectAsync(_options.Host, _options.Port).Wait(Timeout))
                    return;

                using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                    (_, certificate, _, _) =>
                    {
                        // Citit acum, cât timp certificatul e valid: după
                        // închiderea conexiunii, SslStream îl eliberează.
                        if (certificate is not null)
                            _lastServerCertificate = ServerCertificateInfo.From(certificate);
                        return true;   // doar citim; conexiunea se închide imediat
                    });

                ssl.AuthenticateAsClient(_options.Host);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LDAP: certificatul serverului nu a putut fi citit pentru afisare");
            }
        }

        private static void Bind(LdapConnection connection, string name, string password)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                connection.Bind();
                return;
            }

            connection.Bind(new NetworkCredential(name, password));
        }

        /// <summary>
        /// Validarea certificatului DC-ului. Trei moduri, în ordinea strictaței:
        /// amprentă fixată, CA propriu, magazinul de încredere al sistemului.
        /// „Acceptă orice” există doar pentru laborator și e refuzat de
        /// Program.cs în afara mediului Development.
        /// </summary>
        private bool Validate(X509Certificate certificate)
        {
            var cert = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
            _lastServerCertificate = ServerCertificateInfo.From(cert);

            if (!string.IsNullOrWhiteSpace(_options.ServerCertificateThumbprint))
            {
                var expected = _options.ServerCertificateThumbprint
                    .Replace(":", string.Empty).Replace(" ", string.Empty).Trim();

                var actual = Thumbprint(cert);
                var match  = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    _logger.LogError(
                        "LDAP: amprenta certificatului nu corespunde. Asteptat {Expected}, primit {Actual}",
                        expected, actual);
                }

                return match;
            }

            if (!string.IsNullOrWhiteSpace(_options.CaCertificatePath))
            {
                try
                {
                    // Rădăcină de încredere proprie: certificatul DC-ului trebuie
                    // să ducă exact la CA-ul dat, nu la unul din magazinul
                    // sistemului. Exact cazul Samba AD, care își semnează singur
                    // certificatul cu un CA generat la provizionare.
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(
                        new X509Certificate2(_options.CaCertificatePath));
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                    // Lanțul valid nu ajunge: orice certificat emis de același CA
                    // (de exemplu al altui server din domeniu) ar trece. Numele
                    // din certificat trebuie să fie exact LDAP_HOST.
                    var valid = chain.Build(cert) && MatchesHost(cert);
                    if (!valid)
                    {
                        _logger.LogError("LDAP: certificatul serverului nu se validează cu CA-ul din {Path}: {Status}",
                            _options.CaCertificatePath,
                            string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())));
                    }
                    return valid;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LDAP: CA-ul din {Path} nu a putut fi citit", _options.CaCertificatePath);
                    return false;
                }
            }

            if (_options.AllowUntrustedCertificate)
            {
                _logger.LogWarning(
                    "LDAP: certificat ACCEPTAT FĂRĂ VERIFICARE ({Subject}, amprenta {Thumbprint}). " +
                    "Conexiunea nu e protejată împotriva unui atac de tip om la mijloc.",
                    cert.Subject, Thumbprint(cert));
                return true;
            }

            using var systemChain = new X509Chain();
            systemChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var trusted = systemChain.Build(cert) && MatchesHost(cert);

            if (!trusted)
            {
                _logger.LogError(
                    "LDAP: certificatul {Subject} nu e de încredere. Fixați amprenta " +
                    "(LDAP_CERT_THUMBPRINT={Thumbprint}) sau indicați CA-ul (LDAP_CA_FILE).",
                    cert.Subject, Thumbprint(cert));
            }

            return trusted;
        }

        private bool MatchesHost(X509Certificate2 cert)
        {
            var match = cert.MatchesHostname(_options.Host);
            if (!match)
            {
                _logger.LogError("LDAP: certificatul {Subject} nu este emis pentru {Host}. " +
                                 "LDAP_HOST trebuie sa fie numele din certificat, nu adresa IP.",
                    cert.Subject, _options.Host);
            }
            return match;
        }

        private static string? Thumbprint(X509Certificate2? cert)
        {
            if (cert is null) return null;
            // GetCertHashString(SHA256) da acelasi sir ca `openssl x509 -fingerprint -sha256`,
            // fara „:” - exact ce se pune in LDAP_CERT_THUMBPRINT.
            return cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Citirea atributelor
        // ═════════════════════════════════════════════════════════════════════

        private SearchResultEntry? FindEntry(LdapConnection connection, string account)
        {
            // Numele tastat se escapează conform RFC 4515. Fără asta, un nume ca
            // „*” ar întoarce primul cont din domeniu.
            var filter = string.Format(
                CultureInfo.InvariantCulture, _options.UserFilter, LdapFilter.Escape(account));

            var attributes = UserAttributes.ToList();
            if (!string.IsNullOrWhiteSpace(_options.DepartmentAttribute) &&
                !attributes.Contains(_options.DepartmentAttribute, StringComparer.OrdinalIgnoreCase))
            {
                attributes.Add(_options.DepartmentAttribute);
            }

            var request = new SearchRequest(
                _options.EffectiveUserSearchBase, filter, SearchScope.Subtree, attributes.ToArray());

            var response = (SearchResponse)connection.SendRequest(request, Timeout);

            if (response.Entries.Count == 0) return null;

            if (response.Entries.Count > 1)
            {
                // Două conturi cu același sAMAccountName nu pot exista într-un
                // domeniu. Dacă se întâmplă, filtrul e prea larg: refuzăm, ca să
                // nu autentificăm un cont ales la întâmplare.
                _logger.LogError("LDAP: filtrul a intors {Count} conturi pentru {Account}. Verificati Ldap:UserFilter.",
                    response.Entries.Count, account);
                return null;
            }

            return response.Entries[0];
        }

        private DirectoryUser ReadUser(LdapConnection connection, SearchResultEntry entry)
        {
            var dn     = entry.DistinguishedName;
            var sam    = First(entry, "sAMAccountName") ?? NameFromDn(dn);
            var groups = Multi(entry, "memberOf").ToList();

            if (_options.ResolveNestedGroups)
            {
                try
                {
                    foreach (var nested in NestedGroups(connection, dn))
                        if (!groups.Contains(nested, StringComparer.OrdinalIgnoreCase))
                            groups.Add(nested);
                }
                catch (Exception ex)
                {
                    // Regula de potrivire în lanț nu e obligatorie în orice server
                    // LDAP. Dacă nu merge, rămân apartenențele directe - mai puțin,
                    // dar corect; niciodată mai multe drepturi decât se cuvine.
                    _logger.LogWarning(ex,
                        "LDAP: grupurile indirecte nu au putut fi rezolvate pentru {Dn}. " +
                        "Se folosesc doar apartenentele directe din memberOf.", dn);
                }
            }

            var uac      = Long(entry, "userAccountControl") ?? 0;
            var disabled = (uac & 0x2) != 0;            // ACCOUNTDISABLE
            var expired  = (uac & 0x800000) != 0;       // PASSWORD_EXPIRED

            var pwdLastSet = Long(entry, "pwdLastSet");

            return new DirectoryUser(
                ObjectId:          ObjectGuid(entry) ?? dn,
                SamAccountName:    sam,
                DistinguishedName: dn,
                DisplayName:       First(entry, "displayName") ?? First(entry, "cn"),
                Email:             First(entry, "mail"),
                Department:        string.IsNullOrWhiteSpace(_options.DepartmentAttribute)
                                       ? null
                                       : First(entry, _options.DepartmentAttribute),
                UserPrincipalName: First(entry, "userPrincipalName"),
                Groups:            groups,
                Enabled:           !disabled,
                PasswordSetAt:     FromFileTime(pwdLastSet),
                MustChangePasswordAtNextLogon: pwdLastSet == 0 || expired);
        }

        private IEnumerable<string> NestedGroups(LdapConnection connection, string userDn)
        {
            var filter = $"(&(objectClass=group)(member:{MatchingRuleInChain}:={LdapFilter.Escape(userDn)}))";

            var response = (SearchResponse)connection.SendRequest(
                new SearchRequest(_options.BaseDn, filter, SearchScope.Subtree, "distinguishedName"),
                Timeout);

            foreach (SearchResultEntry group in response.Entries)
                yield return group.DistinguishedName;
        }

        private static string? First(SearchResultEntry entry, string attribute)
        {
            var values = entry.Attributes[attribute];
            if (values is null || values.Count == 0) return null;

            var value = values[0]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static IEnumerable<string> Multi(SearchResultEntry entry, string attribute)
        {
            var values = entry.Attributes[attribute];
            if (values is null) yield break;

            foreach (var value in values.GetValues(typeof(string)).Cast<string>())
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }

        private static long? Long(SearchResultEntry entry, string attribute) =>
            long.TryParse(First(entry, attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        /// <summary>objectGUID vine ca octeți, nu ca text - altfel s-ar citi ca mojibake.</summary>
        private static string? ObjectGuid(SearchResultEntry entry)
        {
            var values = entry.Attributes["objectGUID"];
            if (values is null || values.Count == 0) return null;

            // GetValues e declarat object[], dar continutul real difera intre
            // implementari (byte[][] pe unele, object[] cu byte[] in interior pe
            // altele). Se trateaza ambele: altfel identificatorul stabil al
            // contului s-ar pierde tacut, iar o redenumire in AD ar crea un cont
            // local nou, cu chei noi.
            object[] raw;
            try
            {
                raw = values.GetValues(typeof(byte[]));
            }
            catch (NotSupportedException)
            {
                return null;
            }

            if (raw.Length > 0 && raw[0] is byte[] { Length: 16 } bytes)
                return new Guid(bytes).ToString();

            return null;
        }

        /// <summary>
        /// pwdLastSet e FILETIME (intervale de 100 ns de la 1601). Zero înseamnă
        /// „trebuie schimbată la următorul logon”, nu o dată calendaristică.
        /// </summary>
        private static DateTime? FromFileTime(long? fileTime)
        {
            if (fileTime is null or <= 0) return null;

            try { return DateTime.FromFileTimeUtc(fileTime.Value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static string? ParentDn(string dn)
        {
            var comma = IndexOfUnescapedComma(dn);
            return comma < 0 || comma == dn.Length - 1 ? null : dn[(comma + 1)..].Trim();
        }

        private static int DepthOf(string dn, HashSet<string> known)
        {
            var depth   = 0;
            var current = ParentDn(dn);

            while (current is not null && known.Contains(current))
            {
                depth++;
                current = ParentDn(current);
            }

            return depth;
        }

        private static string NameFromDn(string dn)
        {
            var first = dn.Split(',')[0];
            var eq    = first.IndexOf('=');
            return eq >= 0 ? first[(eq + 1)..].Trim() : first.Trim();
        }

        /// <summary>O virgulă precedată de „\” face parte din valoare, nu separă componentele DN-ului.</summary>
        private static int IndexOfUnescapedComma(string dn)
        {
            for (var i = 0; i < dn.Length; i++)
                if (dn[i] == ',' && (i == 0 || dn[i - 1] != '\\')) return i;
            return -1;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Traducerea erorilor AD
        // ═════════════════════════════════════════════════════════════════════

        private enum LdapError
        {
            InvalidCredentials = 49,
        }

        /// <summary>
        /// AD întoarce același cod 49 pentru orice refuz, iar motivul real stă
        /// într-un „data XXX” din mesaj. Traducerea nu ajunge la utilizator
        /// (mesajul lui rămâne generic), dar în jurnalul de audit face diferența
        /// între „a greșit parola” și „contul e dezactivat de o lună”.
        /// </summary>
        private static DirectoryAuthResult Classify(LdapException ex)
        {
            var message = ex.Message ?? string.Empty;

            if (message.Contains("data 525", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.UserNotFound, "AD: contul nu exista (525)");

            if (message.Contains("data 52e", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.InvalidCredentials, "AD: parola incorecta (52e)");

            if (message.Contains("data 530", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.AccountDisabled, "AD: autentificare in afara orarului permis (530)");

            if (message.Contains("data 531", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.AccountDisabled, "AD: autentificare de pe o statie nepermisa (531)");

            if (message.Contains("data 532", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.PasswordExpired, "AD: parola expirata (532)");

            if (message.Contains("data 533", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.AccountDisabled, "AD: cont dezactivat (533)");

            if (message.Contains("data 701", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.AccountDisabled, "AD: cont expirat (701)");

            if (message.Contains("data 773", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.PasswordExpired, "AD: parola trebuie schimbata la logon (773)");

            if (message.Contains("data 775", StringComparison.OrdinalIgnoreCase))
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.AccountLocked, "AD: cont blocat (775)");

            if (ex.ErrorCode == (int)LdapError.InvalidCredentials)
                return DirectoryAuthResult.Fail(DirectoryAuthStatus.InvalidCredentials, "AD: bind respins (49)");

            return DirectoryAuthResult.Fail(DirectoryAuthStatus.ServerUnavailable,
                $"AD: eroare LDAP {ex.ErrorCode} - {Shorten(message)}");
        }

        /// <summary>Mesajele de rețea pot fi lungi; în audit intră o coloană, nu un eseu.</summary>
        private static string Shorten(string message) =>
            message.Length <= 200 ? message : message[..200];
    }
}

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Subiectul și amprenta SHA-256 ale certificatului unui controler de
    /// domeniu, copiate ca text. Separat de certificat intenționat: obiectul
    /// primit în callbackurile TLS e eliberat de SslStream, textul nu.
    /// </summary>
    public sealed record ServerCertificateInfo(string Subject, string Thumbprint)
    {
        public static ServerCertificateInfo From(X509Certificate certificate)
        {
            // Copie proprie din octeții certificatului: nu depinde de cine
            // eliberează originalul, nici de tipul concret primit (X509Certificate
            // simplu pe unele platforme).
            using var copy = new X509Certificate2(certificate.GetRawCertData());

            // SHA-256 fără „:”, același șir ca `openssl x509 -fingerprint -sha256`,
            // exact ce se pune în LDAP_CERT_THUMBPRINT.
            return new ServerCertificateInfo(
                copy.Subject,
                copy.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        }
    }
}
