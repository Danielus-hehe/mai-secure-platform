using MAI.BusinessLogic.Security;

namespace MAI.Api.Options
{
    /// <summary>
    /// Configurarea serverului SMTP pentru notificări email.
    /// Se mapează din secțiunea "Smtp" din appsettings.json.
    /// </summary>
    public class SmtpOptions
    {
        /// <summary>Adresa serverului SMTP (ex. "smtp.gmail.com").</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>Portul SMTP. 465 pentru SSL implicit, 587 pentru STARTTLS.</summary>
        public int Port { get; set; } = 465;

        /// <summary>True = SSL implicit (port 465). False = STARTTLS (port 587).</summary>
        public bool UseSsl { get; set; } = true;

        /// <summary>Adresa de email din câmpul From.</summary>
        public string From { get; set; } = string.Empty;

        /// <summary>Numele afișat în câmpul From (ex. "SGDM MAI").</summary>
        public string DisplayName { get; set; } = "SGDM MAI";

        /// <summary>Utilizatorul pentru autentificarea SMTP.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Parola pentru autentificarea SMTP (din variabilă de mediu).</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// True doar dacă toate câmpurile obligatorii sunt completate cu valori
        /// reale, nu cu valorile-șablon din appsettings.json.
        ///
        /// Înainte verifica doar că nu sunt goale. „YOUR_SMTP_HOST_HERE” nu e gol,
        /// deci serviciul se considera configurat și încerca o conexiune TLS spre
        /// un host inexistent la fiecare transfer și la fiecare cont nou - iar
        /// contul nou rămânea neactivat, cu o invitație care nu plecase niciodată.
        /// </summary>
        public bool IsConfigured =>
            IsReal(Host) &&
            IsReal(From) &&
            IsReal(Username) &&
            IsReal(Password) &&
            Port is > 0 and <= 65535;

        /// <summary>
        /// Lista câmpurilor care lipsesc sau au rămas pe valoarea-șablon - pentru
        /// mesajul de la pornire, ca administratorul să știe exact ce să corecteze.
        /// Parola apare doar ca nume de câmp, niciodată cu valoarea.
        /// </summary>
        public IReadOnlyList<string> MissingFields()
        {
            var missing = new List<string>();
            if (!IsReal(Host))     missing.Add("Smtp:Host");
            if (!IsReal(From))     missing.Add("Smtp:From");
            if (!IsReal(Username)) missing.Add("Smtp:Username");
            if (!IsReal(Password)) missing.Add("Smtp:Password (MAI_SMTP_PASSWORD)");
            if (Port is <= 0 or > 65535) missing.Add("Smtp:Port");
            return missing;
        }

        private static bool IsReal(string? value) =>
            !string.IsNullOrWhiteSpace(value) && !PlaceholderSecrets.IsPlaceholder(value);
    }
}