using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.DataAccessLayer.Maintenance;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Tools
{
    /// <summary>
    /// Aplică migrările EF și pregătește rolul PostgreSQL al aplicației:
    ///
    /// <code>
    /// dotnet run --project MAI.Api -- db:migrate [--list]
    /// docker compose run --rm migrate
    /// docker compose run --rm migrate db:migrate --list
    /// </code>
    ///
    /// De ce o comandă a aplicației și nu „dotnet ef database update”: dotnet ef
    /// cere SDK-ul .NET și codul sursă pe mașina care face instalarea. Imaginea
    /// API-ului conține deja migrările compilate, deci serverul are nevoie doar
    /// de Docker. În docker-compose, serviciul „migrate” rulează comanda înaintea
    /// API-ului, la fiecare „up”; API-ul pornește doar dacă ea a reușit.
    ///
    /// De ce nu la pornirea API-ului: o schimbare de schemă se face cu drepturile
    /// proprietarului, iar API-ul rulează intenționat FĂRĂ ele (rolul aplicației,
    /// vezi <see cref="DatabaseRoleProvisioner"/>). Două procese, două roluri.
    ///
    /// Configurarea nu trece prin Program.cs: comanda are nevoie doar de conexiune
    /// și de rolul aplicației, nu de cheia JWT, pepper sau SMTP. Containerul
    /// „migrate” primește numai ce folosește.
    ///
    /// Configurare:
    ///   ConnectionStrings:DefaultConnection  conexiunea PROPRIETARULUI schemei
    ///   Database:AppUser                     rolul aplicației (POSTGRES_APP_USER)
    ///   MAI_DB_APP_PASSWORD                  parola lui (POSTGRES_APP_PASSWORD)
    ///   Database:RequireAppRole              true în Docker: fără rol, eroare
    /// </summary>
    public static class DbMigrateCommand
    {
        public const string Verb = "db:migrate";

        public const string AppPasswordVariable = "MAI_DB_APP_PASSWORD";

        public static bool IsMigrate(string[] args) =>
            args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parametrii comenzii, citiți dintr-o configurație. Separat de rulare, ca
        /// regulile (ce e obligatoriu, ce e refuzat) să se testeze fără bază.
        /// </summary>
        public sealed record Settings(
            string? ConnectionString,
            string? AppUser,
            string? AppPassword,
            bool RequireAppRole,
            bool ListOnly);

        public static Settings ReadSettings(IConfiguration configuration, string[] args) => new(
            ConnectionString: configuration.GetConnectionString("DefaultConnection"),
            AppUser:          Blank(configuration["Database:AppUser"]),
            AppPassword:      Blank(Environment.GetEnvironmentVariable(AppPasswordVariable))
                              ?? Blank(configuration["Database:AppPassword"]),
            RequireAppRole:   configuration.GetValue("Database:RequireAppRole", false),
            ListOnly:         args.Skip(1).Any(a => string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// Verifică parametrii înainte de orice conexiune. Întoarce mesajul de
        /// eroare sau null. În afara mediului Development, valorile-șablon din
        /// .env.example sunt refuzate: sunt publice în repository.
        /// </summary>
        public static string? Validate(Settings settings, bool isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(settings.ConnectionString) ||
                settings.ConnectionString.StartsWith("YOUR_", StringComparison.Ordinal))
                return "ConnectionStrings:DefaultConnection lipsește (DB_CONNECTION_STRING în .env).";

            if (!isDevelopment && PlaceholderSecrets.IsPlaceholder(settings.ConnectionString))
                return "Parola proprietarului bazei are încă valoarea-șablon (POSTGRES_PASSWORD în .env).";

            if (settings.ListOnly) return null;

            var hasUser     = settings.AppUser is not null;
            var hasPassword = settings.AppPassword is not null;

            if (settings.RequireAppRole && (!hasUser || !hasPassword))
                return "Rolul aplicației este obligatoriu aici: setați POSTGRES_APP_USER și POSTGRES_APP_PASSWORD în .env " +
                       $"(parola: minim {DatabaseRoleProvisioner.MinPasswordLength} caractere, " +
                       "de ex. docker run --rm alpine/openssl rand -hex 24).";

            if (hasUser != hasPassword)
                return "POSTGRES_APP_USER și POSTGRES_APP_PASSWORD se setează împreună.";

            if (hasPassword && !isDevelopment && PlaceholderSecrets.IsPlaceholder(settings.AppPassword))
                return "POSTGRES_APP_PASSWORD are încă valoarea-șablon din .env.example. Generați una nouă.";

            return null;
        }

        /// <summary>Codul de ieșire: 0 = reușit, 1 = eroare, 2 = configurare greșită.</summary>
        public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
        {
            // Aceleași surse de configurare ca API-ul (appsettings, user-secrets în
            // Development, variabile de mediu, deci și .env prin DotEnvLoader), dar
            // fără restul validărilor din Program.cs.
            var builder  = WebApplication.CreateBuilder(Array.Empty<string>());
            var settings = ReadSettings(builder.Configuration, args);

            var error = Validate(settings, builder.Environment.IsDevelopment());
            if (error is not null)
            {
                Console.Error.WriteLine($"db:migrate: {error}");
                return 2;
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(settings.ConnectionString)
                .Options;

            try
            {
                await using var db = new AppDbContext(options);

                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

                Console.WriteLine($"Migrari aplicate: {applied.Count}. In asteptare: {pending.Count}.");
                foreach (var name in pending)
                    Console.WriteLine($"  - {name}");

                if (settings.ListOnly) return 0;

                if (pending.Count > 0)
                {
                    await db.Database.MigrateAsync(ct);
                    Console.WriteLine($"Aplicate acum: {pending.Count}.");
                }

                if (settings.AppUser is null)
                {
                    // Local, cu dotnet run, e normal: API-ul folosește conexiunea din
                    // DB_CONNECTION_STRING. În Docker, RequireAppRole a oprit deja
                    // rularea mai sus.
                    Console.WriteLine("Rolul aplicatiei nu este configurat (POSTGRES_APP_USER): drepturile nu se modifica.");
                    return 0;
                }

                var report = await DatabaseRoleProvisioner.EnsureAppRoleAsync(
                    db, settings.AppUser, settings.AppPassword!, ct);

                Console.WriteLine(
                    $"Rolul aplicatiei '{report.RoleName}' {(report.Created ? "creat" : "actualizat")}: " +
                    $"citire={Yes(report.CanReadUsers)}, jurnal INSERT={Yes(report.CanInsertAudit)}, " +
                    $"jurnal UPDATE={Yes(report.CanUpdateAudit)}, jurnal DELETE={Yes(report.CanDeleteAudit)}, " +
                    $"istoric migrari scriere={Yes(report.CanWriteMigrationHistory)}.");

                if (report.RowSecurityDisabled.Count > 0)
                {
                    // O singură dată pe o bază venită de pe Supabase; la rulările
                    // următoare lista e goală.
                    Console.WriteLine(
                        $"Row Level Security dezactivat (activ fara nicio politica, ramas de pe Supabase): " +
                        string.Join(", ", report.RowSecurityDisabled));
                }

                if (report.RowSecurityWithPolicies.Count > 0)
                {
                    // Politicile se evaluează pentru rolul aplicației, care nu le
                    // cunoaște: rezultatul ar fi rânduri invizibile sau INSERT-uri
                    // refuzate, adică exact un login cu 500. Mai bine oprit aici,
                    // cu numele tabelelor, decât descoperit la prima cerere.
                    Console.Error.WriteLine(
                        "db:migrate: tabele cu Row Level Security si politici: " +
                        string.Join(", ", report.RowSecurityWithPolicies) +
                        ". Aplicatia nu foloseste RLS; stergeti politicile (DROP POLICY) sau dezactivati RLS pe ele.");
                    return 1;
                }

                // Verificarea finală se face pe drepturile efective, nu pe faptul că
                // instrucțiunile au rulat: un rol moștenit sau un GRANT manual făcut
                // cândva ar putea anula REVOKE-ul fără nicio eroare.
                if (!report.IsLeastPrivilege)
                {
                    Console.Error.WriteLine(
                        "db:migrate: rolul aplicatiei are alte drepturi decat cele asteptate. " +
                        "Verificati apartenenta la alte roluri: \\du in psql.");
                    return 1;
                }

                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"db:migrate a esuat: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Yes(bool value) => value ? "da" : "nu";
    }
}
