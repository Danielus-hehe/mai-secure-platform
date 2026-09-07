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
        };

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

            if (CommonPasswords.Contains(password))
                result.Errors.Add("Parola este prea comună și ușor de ghicit.");

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