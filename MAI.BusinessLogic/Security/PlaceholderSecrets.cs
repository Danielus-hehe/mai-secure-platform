namespace MAI.BusinessLogic.Security
{
    /// <summary>
    /// Recunoaște valorile-șablon din fișierele versionate în Git
    /// (<c>appsettings.json</c>, <c>.env.example</c>,
    /// <c>appsettings.Development.json.example</c>).
    ///
    /// Un secret rămas pe valoarea-șablon e un secret public: oricine are acces
    /// la repository îl cunoaște. Pentru cheia JWT asta înseamnă tokenuri de
    /// Administrator fabricate de oricine; pentru pepper, un pepper care nu mai
    /// protejează nimic.
    ///
    /// Verificarea caută marcajele folosite în șabloane, nu o listă exactă de
    /// valori. O listă exactă scapă exact cazul care contează: șablonul se
    /// schimbă într-un fișier, iar lista din cod rămâne în urmă. Așa au trecut
    /// de validare valorile <c>GENERATI_CU_openssl_rand_base64_48</c>, deși
    /// <c>YOUR_JWT_SECRET_KEY_HERE</c> era respinsă.
    ///
    /// Comparația e sensibilă la majuscule: șabloanele folosesc majuscule, iar un
    /// secret generat cu <c>openssl rand -base64</c> nu conține caracterul „_”,
    /// deci nu poate produce din întâmplare un marcaj.
    /// </summary>
    public static class PlaceholderSecrets
    {
        private static readonly string[] Markers =
        {
            "YOUR_",        // appsettings.json
            "GENERATI_",    // .env.example, appsettings.Development.json.example
            "SCHIMBA_MA",   // parolele-șablon pentru PostgreSQL și MinIO
            "CHANGE_ME",
            "CHANGEME",
        };

        /// <summary>True dacă valoarea conține un marcaj de șablon.</summary>
        public static bool IsPlaceholder(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            Markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }
}
