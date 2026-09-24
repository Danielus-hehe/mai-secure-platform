using MAI.BusinessLogic.Security;

namespace MAI.Api.Configuration
{
    /// <summary>
    /// Încarcă fișierul <c>.env</c> din rădăcina repository-ului când API-ul
    /// rulează local (<c>dotnet run</c>, <c>dotnet ef</c>, Rider/Visual Studio).
    ///
    /// Scopul: O SINGURĂ sursă de configurare pentru ambele moduri de rulare.
    /// Docker Compose citește <c>.env</c> și îl traduce în variabile de mediu
    /// (vezi <c>docker-compose.yml</c>). Fără clasa asta, rularea locală avea
    /// nevoie de aceleași valori a doua oară, în <c>user-secrets</c> sau în
    /// <c>appsettings.Development.json</c> - două locuri care se desincronizează.
    ///
    /// Reguli:
    /// <list type="bullet">
    ///   <item>În container nu face nimic: acolo variabilele vin deja de la
    ///   Compose, iar <c>.env</c> e exclus din imagine prin <c>.dockerignore</c>.</item>
    ///   <item>O variabilă de mediu deja setată NU e suprascrisă. Ce setezi
    ///   explicit în terminal sau în sistem are prioritate față de fișier.</item>
    ///   <item>Valorile goale și valorile-șablon sunt ignorate: <c>SMTP_HOST=</c>
    ///   sau <c>MAI_JWT_KEY=GENERATI_...</c> înseamnă „nesetat”, nu „suprascrie
    ///   valoarea din appsettings”.</item>
    ///   <item>Cheile în stilul Compose (<c>SMTP_HOST</c>, <c>DB_CONNECTION_STRING</c>)
    ///   sunt traduse în cheile de configurare ASP.NET (<c>Smtp__Host</c>,
    ///   <c>ConnectionStrings__DefaultConnection</c>) cu ACEEAȘI mapare ca în
    ///   <c>docker-compose.yml</c>. Dacă adaugi o variabilă acolo, adaug-o și în
    ///   <see cref="ComposeMapping"/>.</item>
    /// </list>
    ///
    /// Trebuie apelată ÎNAINTE de <c>WebApplication.CreateBuilder</c>: provider-ul
    /// de variabile de mediu își face instantaneul în momentul construirii.
    /// </summary>
    public static class DotEnvLoader
    {
        /// <summary>
        /// Cheie din <c>.env</c> → cheile de configurare în care se copiază.
        /// Oglinda secțiunii <c>environment:</c> a serviciului <c>api</c>.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> ComposeMapping =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["DB_CONNECTION_STRING"] = new[] { "ConnectionStrings__DefaultConnection" },
                ["FRONTEND_ORIGIN"]      = new[] { "Cors__AllowedOrigins__0", "Frontend__BaseUrl" },
                ["STORAGE_BUCKET"]       = new[] { "Storage__Bucket" },
                ["MINIO_ROOT_USER"]      = new[] { "MAI_STORAGE_ACCESS_KEY" },
                ["MINIO_ROOT_PASSWORD"]  = new[] { "MAI_STORAGE_SECRET_KEY" },
                ["SMTP_HOST"]            = new[] { "Smtp__Host" },
                ["SMTP_PORT"]            = new[] { "Smtp__Port" },
                ["SMTP_USE_SSL"]         = new[] { "Smtp__UseSsl" },
                ["SMTP_FROM"]            = new[] { "Smtp__From" },
                ["SMTP_USERNAME"]        = new[] { "Smtp__Username" },
                ["TRANSFER_DEFAULT_EXPIRY_DAYS"] = new[] { "Transfers__DefaultExpiryDays" },
                ["TRANSFER_MAX_EXPIRY_DAYS"]     = new[] { "Transfers__MaxExpiryDays" },
                ["TRANSFER_MAX_RECIPIENTS"]      = new[] { "Transfers__MaxRecipients" },
                ["PASSWORD_RESET_TOKEN_MINUTES"] = new[] { "PasswordReset__TokenMinutes" },
                ["SESSION_ABSOLUTE_HOURS"]       = new[] { "Jwt__SessionAbsoluteHours" },
                ["STORAGE_ENCRYPTION_ENABLED"]         = new[] { "StorageEncryption__Enabled" },
                ["STORAGE_ENCRYPTION_ACTIVE_KEY"]      = new[] { "StorageEncryption__ActiveKeyId" },
                ["STORAGE_ENCRYPTION_ALLOW_PLAINTEXT"] = new[] { "StorageEncryption__AllowPlaintextRead" },

                // Active Directory. Parola contului de serviciu
                // (MAI_LDAP_BIND_PASSWORD) e citita direct de Program.cs, ca si
                // celelalte secrete, deci nu are nevoie de mapare.
                ["LDAP_ENABLED"]              = new[] { "Ldap__Enabled" },
                ["LDAP_HOST"]                 = new[] { "Ldap__Host" },
                ["LDAP_PORT"]                 = new[] { "Ldap__Port" },
                ["LDAP_USE_LDAPS"]            = new[] { "Ldap__UseLdaps" },
                ["LDAP_USE_STARTTLS"]         = new[] { "Ldap__UseStartTls" },
                ["LDAP_ALLOW_PLAINTEXT"]      = new[] { "Ldap__AllowInsecurePlaintext" },
                ["LDAP_BASE_DN"]              = new[] { "Ldap__BaseDn" },
                ["LDAP_USER_SEARCH_BASE"]     = new[] { "Ldap__UserSearchBase" },
                ["LDAP_OU_SEARCH_BASE"]       = new[] { "Ldap__OrgUnitSearchBase" },
                ["LDAP_USER_FILTER"]          = new[] { "Ldap__UserFilter" },
                ["LDAP_REALM"]                = new[] { "Ldap__Realm" },
                ["LDAP_NETBIOS_DOMAIN"]       = new[] { "Ldap__NetbiosDomain" },
                ["LDAP_BIND_DN"]              = new[] { "Ldap__BindDn" },
                ["LDAP_GROUP_ROLE_MAPPINGS"]  = new[] { "Ldap__GroupRoleMappings" },
                ["LDAP_DEFAULT_ROLE"]         = new[] { "Ldap__DefaultRole" },
                ["LDAP_AUTO_CREATE"]          = new[] { "Ldap__AutoCreateUsers" },
                ["LDAP_SYNC_ON_LOGIN"]        = new[] { "Ldap__SyncOnLogin" },
                ["LDAP_NESTED_GROUPS"]        = new[] { "Ldap__ResolveNestedGroups" },
                ["LDAP_DEPARTMENT_ATTRIBUTE"] = new[] { "Ldap__DepartmentAttribute" },
                ["LDAP_CERT_THUMBPRINT"]      = new[] { "Ldap__ServerCertificateThumbprint" },
                ["LDAP_CA_FILE"]              = new[] { "Ldap__CaCertificatePath" },
                ["LDAP_ALLOW_UNTRUSTED_CERT"] = new[] { "Ldap__AllowUntrustedCertificate" },
            };

        /// <summary>Rezultatul încărcării, pentru mesajul din log (fără valori).</summary>
        public sealed record Result(string? Path, int Applied, IReadOnlyList<string> Keys)
        {
            public static readonly Result NotLoaded = new(null, 0, Array.Empty<string>());
        }

        /// <summary>
        /// Caută <c>.env</c> urcând din directorul curent (cel mult 4 niveluri:
        /// <c>MAI.Api/</c> → rădăcina repo-ului) și îl aplică.
        /// </summary>
        public static Result LoadFromRepositoryRoot()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
                    StringComparison.OrdinalIgnoreCase))
                return Result.NotLoaded;

            var path = FindUpwards(Directory.GetCurrentDirectory(), ".env", maxLevels: 4);
            if (path is null) return Result.NotLoaded;

            return Apply(
                path,
                Parse(File.ReadAllText(path)),
                Environment.GetEnvironmentVariable,
                Environment.SetEnvironmentVariable);
        }

        /// <summary>
        /// Aplică perechile citite. Separată de sistemul de fișiere și de
        /// variabilele de mediu reale, ca să poată fi testată.
        /// </summary>
        public static Result Apply(
            string path,
            IReadOnlyDictionary<string, string> values,
            Func<string, string?> get,
            Action<string, string> set)
        {
            var applied = new List<string>();

            void SetIfAbsent(string key, string value)
            {
                if (!string.IsNullOrEmpty(get(key))) return;   // mediul real câștigă
                set(key, value);
                applied.Add(key);
            }

            foreach (var (key, value) in values)
            {
                if (string.IsNullOrEmpty(value)) continue;

                // Valorile-șablon (GENERATI_..., SCHIMBA_MA...) dintr-un .env copiat
                // din .env.example și necompletat nu au voie să suprascrie o
                // configurare locală funcțională. Pentru pepper ar fi fatal: toate
                // parolele existente ar deveni invalide.
                if (PlaceholderSecrets.IsPlaceholder(value)) continue;

                // Cheia originală rămâne disponibilă (ex. MAI_JWT_KEY, citită direct
                // de Program.cs cu Environment.GetEnvironmentVariable).
                SetIfAbsent(key, value);

                if (ComposeMapping.TryGetValue(key, out var targets))
                    foreach (var target in targets)
                        SetIfAbsent(target, value);
            }

            return new Result(path, applied.Count, applied);
        }

        /// <summary>
        /// Interpretează conținutul unui fișier <c>.env</c>: <c>CHEIE=valoare</c>
        /// pe fiecare linie, comentarii cu <c>#</c>, ghilimele opționale.
        /// Un <c>#</c> în interiorul valorii (ex. într-o parolă) e păstrat; doar
        /// un <c>#</c> precedat de spațiu, în afara ghilimelelor, începe un comentariu.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Parse(string content)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line["export ".Length..].TrimStart();

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key   = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();

                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }
                else
                {
                    var comment = value.IndexOf(" #", StringComparison.Ordinal);
                    if (comment >= 0) value = value[..comment].TrimEnd();
                }

                result[key] = value;
            }

            return result;
        }

        private static string? FindUpwards(string start, string fileName, int maxLevels)
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i <= maxLevels && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
