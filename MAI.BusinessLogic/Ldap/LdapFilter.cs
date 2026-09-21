using System;
using System.Text;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Escaparea valorilor puse într-un filtru LDAP și normalizarea numelui de
    /// utilizator tastat la login.
    ///
    /// Escaparea NU este cosmetică. Filtrul se construiește prin interpolare
    /// („(sAMAccountName={0})”), deci un nume ca <c>*</c> ar transforma căutarea
    /// în „primul cont din domeniu”, iar <c>ion)(objectClass=*</c> ar închide
    /// filtrul și ar adăuga o condiție proprie. Este echivalentul LDAP al
    /// injecției SQL și are aceeași rezolvare: valoarea se escapează, nu se
    /// filtrează prin listă neagră.
    /// </summary>
    public static class LdapFilter
    {
        /// <summary>
        /// Escapare conform RFC 4515, secțiunea 3: fiecare octet special devine
        /// „\XX” în hexazecimal. Se lucrează pe octeții UTF-8, nu pe caractere:
        /// pentru un nume cu diacritice, escaparea per caracter ar produce un
        /// filtru pe care serverul îl respinge.
        /// </summary>
        public static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new StringBuilder(value.Length + 8);

            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                switch (b)
                {
                    case (byte)'\\':
                    case (byte)'*':
                    case (byte)'(':
                    case (byte)')':
                    case 0:
                        builder.Append('\\').Append(b.ToString("x2"));
                        break;

                    default:
                        // Octeții peste 0x7F fac parte dintr-un caracter UTF-8 pe
                        // mai mulți octeți; scriși ca atare într-un string .NET ar
                        // fi recodificați greșit, deci se escapează și ei.
                        if (b < 0x20 || b > 0x7E)
                            builder.Append('\\').Append(b.ToString("x2"));
                        else
                            builder.Append((char)b);
                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Escapare pentru un DN folosit ca bază de căutare (RFC 4514). Se aplică
        /// doar valorilor venite din exterior; DN-urile din configurare se
        /// folosesc ca atare.
        /// </summary>
        public static string EscapeDn(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')
                    builder.Append('\\');
                builder.Append(c);
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// Numele tastat la login poate veni în trei forme: „ion.popescu”,
    /// „SGDM\ion.popescu” sau „ion.popescu@sgdm.local”. Toate trei desemnează
    /// același cont, deci și același rând în tabela noastră.
    ///
    /// Fără normalizare, aceeași persoană ar fi creat trei conturi locale la
    /// prima autentificare din fiecare formă, fiecare cu propriile chei E2EE.
    /// </summary>
    public static class DirectoryUsername
    {
        /// <summary>
        /// Reduce orice formă la sAMAccountName. Nu verifică nimic: dacă numele
        /// nu există în domeniu, aflăm de la AD, nu din ghicit.
        /// </summary>
        public static string Normalize(string? raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;

            // DOMENIU\utilizator - se păstrează partea de după bara oblică inversă.
            var backslash = value.LastIndexOf('\\');
            if (backslash >= 0 && backslash < value.Length - 1)
                value = value[(backslash + 1)..];

            // utilizator@realm - se păstrează partea dinaintea arondului.
            var at = value.IndexOf('@');
            if (at > 0)
                value = value[..at];

            return value.Trim();
        }

        /// <summary>
        /// Numele cu care se face bind-ul la AD. UPN-ul e forma cea mai sigură:
        /// funcționează și dacă utilizatorul a fost mutat în alt OU, spre
        /// deosebire de DN. Fără realm configurat, rămâne numele scurt.
        /// </summary>
        public static string ToBindName(string samAccountName, string? realm)
        {
            if (string.IsNullOrWhiteSpace(realm)) return samAccountName;
            if (samAccountName.Contains('@', StringComparison.Ordinal)) return samAccountName;
            return $"{samAccountName}@{realm.Trim().TrimStart('@')}";
        }
    }
}
