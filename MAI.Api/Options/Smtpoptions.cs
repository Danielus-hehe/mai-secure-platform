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

        /// <summary>Returnează true dacă toate câmpurile obligatorii sunt completate.</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Host) &&
            !string.IsNullOrWhiteSpace(From) &&
            !string.IsNullOrWhiteSpace(Username) &&
            !string.IsNullOrWhiteSpace(Password);
    }
}