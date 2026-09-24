using System.Text;
using MAI.Api.Controllers;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Tools
{
    /// <summary>
    /// Creează un cont de administrator direct în baza de date:
    ///
    /// <code>
    /// dotnet run --project MAI.Api -- admin:create &lt;username&gt; [--email adresa] [--name "Nume Prenume"]
    /// docker compose run --rm -it api admin:create &lt;username&gt; [--email adresa] [--name "Nume Prenume"]
    /// </code>
    ///
    /// De ce există: pe o bază goală nu era nicio cale de a obține primul
    /// administrator. Crearea conturilor cere un administrator autentificat, iar
    /// seed-ul demonstrativ copiază hash-ul unui cont care trebuie să existe deja.
    /// Singura ieșire era un INSERT manual cu parolă în clar și
    /// AllowLegacyPlaintext, adică exact configurația pe care API-ul o refuză în
    /// producție.
    ///
    /// Parola:
    ///   • din variabila SGDM_ADMIN_PASSWORD, dacă e setată (automatizare);
    ///   • altfel se cere de la tastatură, de două ori, fără ecou pe ecran.
    /// Niciodată ca argument în linia de comandă: acolo ar rămâne în istoricul
    /// shell-ului și ar fi vizibilă în lista de procese.
    ///
    /// Contul pornește cu MustChangePassword = true, ca orice cont a cărui parolă
    /// a trecut prin mâna altcuiva (operatorul care rulează comanda, o variabilă
    /// de mediu). Titularul își alege parola proprie la prima autentificare, abia
    /// apoi își generează cheile E2EE.
    /// </summary>
    public static class AdminCreateCommand
    {
        public const string Verb = "admin:create";

        /// <summary>Variabila de mediu din care se ia parola, fără prompt.</summary>
        public const string PasswordVariable = "SGDM_ADMIN_PASSWORD";

        public static bool IsAdminCreate(string[] args) =>
            args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

        /// <summary>Codul de ieșire: 0 = creat, 1 = refuzat (date invalide sau duplicate), 2 = utilizare greșită.</summary>
        public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct = default)
        {
            if (!TryParse(args, out var username, out var email, out var fullName, out var usageError))
            {
                Console.Error.WriteLine(usageError);
                Console.Error.WriteLine($"Utilizare: {Verb} <username> [--email adresa] [--name \"Nume Prenume\"]");
                return 2;
            }

            if (!UsersController.UsernameRegex.IsMatch(username))
            {
                Console.Error.WriteLine("Username invalid: 3-50 caractere, litere latine fara diacritice, cifre, '.', '-', '_'.");
                return 1;
            }

            if (!string.IsNullOrEmpty(email) && !UsersController.EmailRegex.IsMatch(email))
            {
                Console.Error.WriteLine("Adresa de email nu este valida.");
                return 1;
            }

            using var scope = services.CreateScope();
            var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var policy = scope.ServiceProvider.GetRequiredService<PasswordPolicy>();
            var argon2 = scope.ServiceProvider.GetRequiredService<Argon2Options>();

            // Unicitatea se verifică la fel ca în UsersController: fără diferență
            // între majuscule și minuscule (indexurile UX_Users_*_Lower).
            var usernameKey = username.ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Username.ToLower() == usernameKey, ct))
            {
                Console.Error.WriteLine($"Username-ul '{username}' exista deja.");
                return 1;
            }

            if (!string.IsNullOrEmpty(email))
            {
                var emailKey = email.ToLowerInvariant();
                if (await db.Users.AnyAsync(u => u.Email.ToLower() == emailKey, ct))
                {
                    Console.Error.WriteLine($"Adresa de email '{email}' este deja folosita.");
                    return 1;
                }
            }

            var password = ReadPassword();
            if (password is null)
            {
                Console.Error.WriteLine("Parolele nu coincid sau nu a fost introdusa nicio parola.");
                return 1;
            }

            var validation = policy.Validate(password, username);
            if (!validation.IsValid)
            {
                Console.Error.WriteLine("Parola nu respecta politica de parole:");
                foreach (var error in validation.Errors)
                    Console.Error.WriteLine($"  - {error}");
                return 1;
            }

            var user = new User
            {
                Id                 = Guid.NewGuid(),
                Username           = username,
                Email              = email ?? string.Empty,
                FullName           = string.IsNullOrWhiteSpace(fullName) ? username : fullName.Trim(),
                // Profilul scump, ca pentru orice cont privilegiat (vezi AuthController.ProfileFor).
                PasswordHash       = await hasher.HashPasswordAsync(password, argon2.PrivilegedProfile, ct),
                Role               = UserRole.Administrator,
                IsActive           = true,
                EmailConfirmed     = true,
                MustChangePassword = true,
                CreatedAt          = DateTime.UtcNow,
            };

            db.Users.Add(user);
            db.AuditLogs.Add(new AuditLog
            {
                UserId    = user.Id,
                Username  = "sistem",
                Action    = AuditAction.UserCreated,
                Details   = $"Cont de administrator creat din linia de comanda (admin:create): @{user.Username}, " +
                            "parola temporara, schimbarea ei este obligatorie la prima autentificare",
                // Warning: un administrator nou apărut în afara interfeței e exact
                // rândul pe care un supervizor trebuie să-l găsească filtrând.
                Result    = AuditResult.Warning,
                IpAddress = "local",
                Timestamp = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(ct);

            Console.WriteLine();
            Console.WriteLine($"Administrator creat: @{user.Username}.");
            Console.WriteLine("La prima autentificare, aplicatia cere alegerea unei parole proprii.");
            return 0;
        }

        private static bool TryParse(
            string[] args, out string username, out string? email, out string? fullName, out string error)
        {
            username = string.Empty;
            email    = null;
            fullName = null;
            error    = string.Empty;

            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];

                if (string.Equals(arg, "--email", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    email = args[++i].Trim();
                else if (string.Equals(arg, "--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    fullName = args[++i];
                else if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Optiune necunoscuta sau fara valoare: {arg}.";
                    return false;
                }
                else if (username.Length == 0)
                    username = arg.Trim();
                else
                {
                    error = $"Argument in plus: {arg}. Parola NU se da ca argument (vezi {PasswordVariable}).";
                    return false;
                }
            }

            if (username.Length == 0)
            {
                error = "Lipseste username-ul.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parola din SGDM_ADMIN_PASSWORD sau, altfel, de la tastatură, de două
        /// ori. Null dacă cele două introduceri nu coincid.
        /// </summary>
        private static string? ReadPassword()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(PasswordVariable);
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                Console.WriteLine($"Parola se ia din {PasswordVariable}.");
                return fromEnvironment;
            }

            var first  = Prompt("Parola: ");
            var second = Prompt("Confirmati parola: ");

            return !string.IsNullOrEmpty(first) && string.Equals(first, second, StringComparison.Ordinal)
                ? first
                : null;
        }

        /// <summary>Citește o linie fără să afișeze caracterele tastate.</summary>
        private static string Prompt(string label)
        {
            Console.Write(label);

            // Intrare redirecționată (pipe, CI): nu există tastatură de mascat.
            if (Console.IsInputRedirected)
                return Console.ReadLine() ?? string.Empty;

            var buffer = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                    break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0) buffer.Length--;
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                    buffer.Append(key.KeyChar);
            }

            Console.WriteLine();
            return buffer.ToString();
        }
    }
}
