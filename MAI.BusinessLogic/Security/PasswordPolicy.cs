using System;
using System.Collections.Generic;
using System.Linq;

namespace MAI.BusinessLogic.Security
{
    /// <summary>Politica de parole aplicată la creare cont și la schimbare parolă.</summary>
    public class PasswordPolicyOptions
    {
        public int MinLength { get; set; } = 12;
        public int MaxLength { get; set; } = 128;
        public bool RequireUppercase { get; set; } = true;
        public bool RequireLowercase { get; set; } = true;
        public bool RequireDigit { get; set; } = true;
        public bool RequireNonAlphanumeric { get; set; } = true;
        public bool ForbidUsernameInPassword { get; set; } = true;
    }

    public class PasswordValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new();
        public string Message => string.Join(" ", Errors);
    }

    public class PasswordPolicy
    {
        private readonly PasswordPolicyOptions _options;

        /// <summary>Listă minimală de parole banale; extinde-o dacă vrei.</summary>
        private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "parola", "123456", "12345678", "123456789", "qwerty",
            "qwertyuiop", "admin", "administrator", "welcome", "iloveyou",
            "parola123", "admin123", "password123", "moldova", "chisinau",
            "politia", "ministerul", "mai2026", "secret",
            "letmein", "parolamea", "parolanoua", "welcomeback", "passwordpassword",
            "sgdm", "internal", "ministry", "politie", "securitate",
        };

        /// <summary>Înlocuiri „leetspeak” uzuale: P@ssw0rd → password.</summary>
        private static readonly Dictionary<char, char> Leet = new()
        {
            ['@'] = 'a', ['4'] = 'a', ['0'] = 'o', ['1'] = 'i', ['!'] = 'i',
            ['3'] = 'e', ['5'] = 's', ['$'] = 's', ['7'] = 't', ['9'] = 'g',
        };

        /// <summary>
        /// True dacă parola e un cuvânt banal cu decor: majusculă la început,
        /// cifre și simboluri la coadă, litere înlocuite cu cifre.
        ///
        /// Înainte se compara doar parola întreagă cu lista. „password123” era
        /// respinsă, dar „Password123!” trecea: are 12 caractere, majusculă, cifră
        /// și simbol, deci bifa toate regulile, deși e printre primele încercate
        /// de orice atac cu dicționar. Regulile de compoziție singure nu opresc
        /// asta; tocmai de aceea NIST SP 800-63B cere verificarea contra
        /// listelor de parole cunoscute.
        ///
        /// Se compară doar „nucleul” întreg cu lista, nu se caută subșiruri:
        /// „Parola-Sigura-2026!” conține „parola”, dar nucleul ei, „parolasigura”,
        /// nu e banal și trebuie acceptat.
        /// </summary>
        private static bool IsCommonVariant(string password)
        {
            if (CommonPasswords.Contains(password)) return true;

            // Decorul de la capete: „Password123!” → „Password”, „!!Admin2026” → „Admin”.
            var start = 0;
            var end   = password.Length - 1;
            while (start <= end && !char.IsLetter(password[start])) start++;
            while (end >= start && !char.IsLetter(password[end]))   end--;
            if (start > end) return false;

            var core = password.Substring(start, end - start + 1);

            // Nucleul așa cum e, fără cifre și simboluri rămase la mijloc.
            var lettersOnly = new string(core.Where(char.IsLetter).ToArray());
            if (CommonPasswords.Contains(lettersOnly)) return true;

            // Nucleul după înlocuirile leetspeak: „P@ssw0rd” → „password”.
            var deLeet = new string(core
                .Select(c => Leet.TryGetValue(char.ToLowerInvariant(c), out var r) ? r : c)
                .Where(char.IsLetter)
                .ToArray());

            return CommonPasswords.Contains(deLeet);
        }

        public PasswordPolicy(PasswordPolicyOptions options) =>
            _options = options ?? new PasswordPolicyOptions();

        public PasswordValidationResult Validate(string? password, string? username = null)
        {
            var result = new PasswordValidationResult();

            if (string.IsNullOrWhiteSpace(password))
            {
                result.Errors.Add("Parola este obligatorie.");
                return result;
            }

            if (password.Length < _options.MinLength)
                result.Errors.Add($"Parola trebuie să aibă minim {_options.MinLength} caractere.");

            if (password.Length > _options.MaxLength)
                result.Errors.Add($"Parola nu poate depăși {_options.MaxLength} caractere.");

            if (_options.RequireUppercase && !password.Any(char.IsUpper))
                result.Errors.Add("Parola trebuie să conțină cel puțin o literă mare.");

            if (_options.RequireLowercase && !password.Any(char.IsLower))
                result.Errors.Add("Parola trebuie să conțină cel puțin o literă mică.");

            if (_options.RequireDigit && !password.Any(char.IsDigit))
                result.Errors.Add("Parola trebuie să conțină cel puțin o cifră.");

            if (_options.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
                result.Errors.Add("Parola trebuie să conțină cel puțin un caracter special.");

            if (IsCommonVariant(password))
                result.Errors.Add("Parola este prea comună și ușor de ghicit (un cuvânt banal cu cifre sau simboluri adăugate rămâne banal).");

            if (_options.ForbidUsernameInPassword
                && !string.IsNullOrWhiteSpace(username)
                && username.Length >= 3
                && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("Parola nu poate conține numele de utilizator.");
            }

            return result;
        }
    }
}