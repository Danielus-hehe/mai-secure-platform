using System;
using System.Collections.Generic;
using System.Linq;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Configurarea autentificării prin Active Directory, din secțiunea „Ldap”
    /// (appsettings.json) sau din .env (LDAP_*, MAI_LDAP_BIND_PASSWORD).
    ///
    /// Implicit DEZACTIVATĂ. Aplicația trebuie să pornească identic într-un
    /// laborator fără domeniu: cât timp Enabled este false, niciun cod de aici
    /// nu se execută, iar conturile locale funcționează exact ca înainte.
    /// </summary>
    public class LdapOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Numele DNS al controlerului de domeniu (nu adresa IP: certificatul e emis pe nume).</summary>
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; } = 636;

        /// <summary>LDAPS (TLS de la primul octet, portul 636).</summary>
        public bool UseLdaps { get; set; } = true;

        /// <summary>StartTLS pe portul 389, alternativă la LDAPS.</summary>
        public bool UseStartTls { get; set; }

        /// <summary>
        /// Permite bind în clar, fără TLS. Există doar pentru diagnosticare pe o
        /// rețea izolată: la un bind simplu, parola trece necriptată prin rețea,
        /// deci un singur sniffer în subrețea DC-ului colectează toate parolele
        /// de domeniu ale instituției. Program.cs refuză pornirea cu valoarea
        /// true în afara mediului Development.
        /// </summary>
        public bool AllowInsecurePlaintext { get; set; }

        /// <summary>Rădăcina domeniului, ex. „DC=sgdm,DC=local”.</summary>
        public string BaseDn { get; set; } = string.Empty;

        /// <summary>Unde se caută conturile. Gol = BaseDn.</summary>
        public string UserSearchBase { get; set; } = string.Empty;

        /// <summary>Unde se caută unitățile organizatorice la import. Gol = BaseDn.</summary>
        public string OrgUnitSearchBase { get; set; } = string.Empty;

        /// <summary>
        /// Filtrul de căutare a contului. <c>{0}</c> se înlocuiește cu numele de
        /// utilizator, escapat conform RFC 4515 (vezi <see cref="LdapFilter"/>).
        /// </summary>
        public string UserFilter { get; set; } =
            "(&(objectClass=user)(objectCategory=person)(sAMAccountName={0}))";

        /// <summary>Sufixul UPN al domeniului, ex. „sgdm.local”. Folosit la normalizarea numelui.</summary>
        public string Realm { get; set; } = string.Empty;

        /// <summary>Numele NetBIOS, ex. „SGDM”. Acceptat ca prefix la login („SGDM\ion.popescu”).</summary>
        public string NetbiosDomain { get; set; } = string.Empty;

        /// <summary>
        /// Contul de serviciu cu care se face căutarea (DN complet sau UPN).
        /// Are nevoie doar de drept de citire: nu scriem niciodată în AD.
        /// </summary>
        public string BindDn { get; set; } = string.Empty;

        /// <summary>Parola contului de serviciu. Din .env (MAI_LDAP_BIND_PASSWORD), nu din appsettings.</summary>
        public string BindPassword { get; set; } = string.Empty;

        /// <summary>
        /// Maparea grupurilor AD pe roluri, ca text:
        /// <c>CN=SGDM-Admins,OU=Grupuri,DC=sgdm,DC=local=Administrator;SGDM-Sefi=SefDirectie</c>
        /// Se acceptă și doar numele grupului (CN), nu doar DN-ul complet.
        /// </summary>
        public string GroupRoleMappings { get; set; } = string.Empty;

        /// <summary>Rolul primit de un cont care nu e în niciun grup mapat.</summary>
        public string DefaultRole { get; set; } = "Utilizator";

        /// <summary>
        /// Creează contul local la prima autentificare reușită. Cu false, doar
        /// conturile legate în prealabil de administrator pot intra.
        /// </summary>
        public bool AutoCreateUsers { get; set; } = true;

        /// <summary>Reia numele, emailul, rolul și subdiviziunea din AD la fiecare autentificare.</summary>
        public bool SyncOnLogin { get; set; } = true;

        /// <summary>
        /// Atributul din care se deduce subdiviziunea. Valoarea lui se caută
        /// printre denumirile și codurile subdiviziunilor existente; dacă nu se
        /// potrivește niciuna, contul rămâne neîncadrat (nu se inventează una).
        /// </summary>
        public string DepartmentAttribute { get; set; } = "department";

        /// <summary>
        /// Caută și apartenențele indirecte la grupuri (grup în grup), prin
        /// regula de potrivire în lanț a AD-ului.
        ///
        /// Atributul memberOf conține doar apartenențele directe. Într-o
        /// instituție, „SGDM-Admins” e aproape sigur un grup care conține alte
        /// grupuri, nu oameni: fără pasul acesta, maparea pe roluri ar părea
        /// configurată corect și n-ar acorda nimănui rolul.
        /// </summary>
        public bool ResolveNestedGroups { get; set; } = true;

        /// <summary>Secunde până la abandonarea unei operații LDAP.</summary>
        public int TimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Amprenta SHA-256 a certificatului serverului (hex, cu sau fără „:”).
        /// Cea mai strictă formă de validare și cea recomandată pentru un DC cu
        /// certificat autosemnat, cum e Samba AD din laborator.
        /// </summary>
        public string ServerCertificateThumbprint { get; set; } = string.Empty;

        /// <summary>Calea unui certificat CA (PEM/DER) în care are încredere clientul.</summary>
        public string CaCertificatePath { get; set; } = string.Empty;

        /// <summary>
        /// Acceptă orice certificat al serverului. Anulează protecția TLS
        /// împotriva unui atac de tip „om la mijloc”: cu un certificat fals,
        /// atacatorul primește parola de domeniu în clar. Permis doar în
        /// Development (verificat în Program.cs).
        /// </summary>
        public bool AllowUntrustedCertificate { get; set; }

        /// <summary>Baza efectivă pentru căutarea conturilor.</summary>
        public string EffectiveUserSearchBase =>
            string.IsNullOrWhiteSpace(UserSearchBase) ? BaseDn : UserSearchBase;

        /// <summary>Baza efectivă pentru căutarea unităților organizatorice.</summary>
        public string EffectiveOrgUnitSearchBase =>
            string.IsNullOrWhiteSpace(OrgUnitSearchBase) ? BaseDn : OrgUnitSearchBase;

        /// <summary>True dacă avem un cont de serviciu pentru căutări.</summary>
        public bool HasServiceAccount =>
            !string.IsNullOrWhiteSpace(BindDn) && !string.IsNullOrWhiteSpace(BindPassword);

        /// <summary>Descrierea conexiunii, pentru loguri și pentru pagina de administrare.</summary>
        public string Endpoint => $"{(UseLdaps ? "ldaps" : "ldap")}://{Host}:{Port}";

        /// <summary>Câmpurile care lipsesc, pentru un mesaj util la pornire.</summary>
        public IReadOnlyList<string> MissingFields()
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(Host))   missing.Add("Ldap:Host");
            if (string.IsNullOrWhiteSpace(BaseDn)) missing.Add("Ldap:BaseDn");
            return missing;
        }

        public void Validate()
        {
            if (!Enabled) return;

            var missing = MissingFields();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "Ldap:Enabled=true, dar lipsesc: " + string.Join(", ", missing) +
                    ". Completați LDAP_HOST și LDAP_BASE_DN în .env sau puneți LDAP_ENABLED=false.");
            }

            if (Port is < 1 or > 65535)
                throw new InvalidOperationException("Ldap:Port trebuie să fie între 1 și 65535.");

            if (UseLdaps && UseStartTls)
            {
                throw new InvalidOperationException(
                    "Ldap:UseLdaps și Ldap:UseStartTls nu pot fi ambele true. " +
                    "LDAPS înseamnă TLS de la început (portul 636), StartTLS îl pornește " +
                    "peste o conexiune deja deschisă (portul 389).");
            }

            // Parola de domeniu nu are voie să treacă în clar prin rețea. Fără
            // regula asta, o configurare greșită (port 389, UseLdaps uitat pe
            // false) ar funcționa perfect la test și ar expune toate parolele.
            if (!UseLdaps && !UseStartTls && !AllowInsecurePlaintext)
            {
                throw new InvalidOperationException(
                    "Ldap: conexiune fără TLS. Parolele de domeniu ar circula în clar. " +
                    "Puneți LDAP_USE_LDAPS=true (portul 636) sau LDAP_USE_STARTTLS=true " +
                    "(portul 389). Doar pentru diagnosticare: LDAP_ALLOW_PLAINTEXT=true.");
            }

            if (TimeoutSeconds is < 1 or > 120)
                throw new InvalidOperationException("Ldap:TimeoutSeconds trebuie să fie între 1 și 120.");

            if (!string.IsNullOrWhiteSpace(UserFilter) && !UserFilter.Contains("{0}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Ldap:UserFilter trebuie să conțină {0}, locul în care se pune numele de utilizator.");
            }

            if (!string.IsNullOrWhiteSpace(CaCertificatePath) &&
                !string.IsNullOrWhiteSpace(ServerCertificateThumbprint))
            {
                throw new InvalidOperationException(
                    "Ldap: alegeți o singură formă de validare a certificatului - " +
                    "fie Ldap:CaCertificatePath, fie Ldap:ServerCertificateThumbprint.");
            }

            // Maparea se interpretează la pornire, nu la primul login: o greșeală
            // de scriere trebuie să apară în logul de pornire, nu într-un refuz
            // inexplicabil de acces peste două zile.
            LdapRoleMapper.Parse(GroupRoleMappings);

            if (!Enum.TryParse<Domain.Enums.UserRole>(DefaultRole, ignoreCase: true, out _))
            {
                throw new InvalidOperationException(
                    $"Ldap:DefaultRole „{DefaultRole}” nu este un rol valid. " +
                    "Valori acceptate: " + string.Join(", ", Enum.GetNames<Domain.Enums.UserRole>()));
            }
        }

        /// <summary>Rolul implicit, deja interpretat.</summary>
        public Domain.Enums.UserRole DefaultRoleValue =>
            Enum.TryParse<Domain.Enums.UserRole>(DefaultRole, ignoreCase: true, out var role)
                ? role
                : Domain.Enums.UserRole.Utilizator;

        /// <summary>Rezumat fără secrete, pentru loguri și pentru pagina de administrare.</summary>
        public IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
        {
            ["endpoint"]        = Endpoint,
            ["baseDn"]          = BaseDn,
            ["userSearchBase"]  = EffectiveUserSearchBase,
            ["serviceAccount"]  = HasServiceAccount ? BindDn : "(fără, se caută cu contul care se autentifică)",
            ["tls"]             = UseLdaps ? "LDAPS" : UseStartTls ? "StartTLS" : "FĂRĂ (nesigur)",
            ["certificate"]     = !string.IsNullOrWhiteSpace(ServerCertificateThumbprint) ? "amprentă fixată"
                                : !string.IsNullOrWhiteSpace(CaCertificatePath) ? "CA propriu"
                                : AllowUntrustedCertificate ? "ORICE (nevalidat)"
                                : "magazinul de încredere al sistemului",
            ["autoCreate"]      = AutoCreateUsers ? "da" : "nu",
            ["syncOnLogin"]     = SyncOnLogin ? "da" : "nu",
            ["defaultRole"]     = DefaultRoleValue.ToString(),
            ["roleMappings"]    = LdapRoleMapper.Parse(GroupRoleMappings).Count.ToString(),
        };
    }
}
