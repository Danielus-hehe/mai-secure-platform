using System;
using System.Collections.Generic;
using System.Linq;
using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>O regulă „grup AD → rol în aplicație”.</summary>
    /// <param name="Group">DN-ul complet al grupului sau doar CN-ul lui.</param>
    /// <param name="Role">Rolul acordat membrilor.</param>
    public sealed record GroupRoleMapping(string Group, UserRole Role);

    /// <summary>
    /// Traduce apartenența la grupuri AD în rolul din aplicație.
    ///
    /// Rolurile NU se administrează în două locuri. Dacă un cont vine din
    /// domeniu, sursa adevărului pentru drepturile lui este AD-ul: altfel, un
    /// om scos din grupul de administratori la plecarea din funcție ar rămâne
    /// administrator la noi, fiindcă nimeni nu ține minte să schimbe și aici.
    ///
    /// Când un cont e în mai multe grupuri mapate, câștigă rolul cel mai mare.
    /// Alternativa (primul din listă) ar face ordinea liniilor din configurare
    /// să conteze silențios.
    /// </summary>
    public static class LdapRoleMapper
    {
        /// <summary>
        /// Interpretează textul din configurare. Format:
        /// <c>grup=Rol;grup=Rol</c>, unde „grup” e un DN complet sau un CN.
        /// Liniile goale se ignoră; o linie greșită oprește pornirea.
        /// </summary>
        public static IReadOnlyList<GroupRoleMapping> Parse(string? specification)
        {
            var result = new List<GroupRoleMapping>();
            if (string.IsNullOrWhiteSpace(specification)) return result;

            foreach (var rawEntry in specification.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = rawEntry.Trim();
                if (entry.Length == 0) continue;

                // Ultimul „=” separă rolul. Primele fac parte din DN
                // („CN=Grup,OU=X,DC=y”), deci Split('=') ar fi rupt DN-ul.
                var separator = entry.LastIndexOf('=');
                if (separator <= 0 || separator == entry.Length - 1)
                {
                    throw new InvalidOperationException(
                        $"Ldap:GroupRoleMappings - intrarea „{entry}” nu are forma grup=Rol.");
                }

                var group = entry[..separator].Trim();
                var role  = entry[(separator + 1)..].Trim();

                if (!Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsed))
                {
                    throw new InvalidOperationException(
                        $"Ldap:GroupRoleMappings - rolul „{role}” din intrarea „{entry}” nu există. " +
                        "Valori acceptate: " + string.Join(", ", Enum.GetNames<UserRole>()));
                }

                result.Add(new GroupRoleMapping(group, parsed));
            }

            return result;
        }

        /// <summary>
        /// Rolul unui cont, din grupurile lui. Comparația nu ține cont de
        /// majuscule și tolerează spațiile din DN-uri („CN=X, OU=Y” vs „CN=X,OU=Y”),
        /// pentru că AD le returnează inconsecvent între instalări.
        /// </summary>
        public static UserRole Resolve(
            IEnumerable<string> groupDns,
            IReadOnlyList<GroupRoleMapping> mappings,
            UserRole defaultRole)
        {
            if (mappings.Count == 0) return defaultRole;

            var groups = groupDns?.Where(g => !string.IsNullOrWhiteSpace(g)).ToList() ?? new List<string>();
            if (groups.Count == 0) return defaultRole;

            var best = defaultRole;

            foreach (var mapping in mappings)
            {
                if (groups.Any(g => Matches(g, mapping.Group)) && mapping.Role > best)
                    best = mapping.Role;
            }

            return best;
        }

        /// <summary>Numele scurt al unui grup, din DN („CN=SGDM-Admins,OU=...” → „SGDM-Admins”).</summary>
        public static string CommonName(string distinguishedName)
        {
            if (string.IsNullOrWhiteSpace(distinguishedName)) return string.Empty;

            var first = distinguishedName.Split(',')[0].Trim();
            return first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)
                ? first[3..].Trim()
                : first;
        }

        private static bool Matches(string groupDn, string configured)
        {
            if (string.Equals(Canonical(groupDn), Canonical(configured), StringComparison.OrdinalIgnoreCase))
                return true;

            // Configurarea poate da doar numele grupului, fără DN. Util pentru
            // laborator, unde DN-ul se schimbă la fiecare reprovizionare a
            // domeniului, iar numele grupului rămâne.
            return !configured.Contains('=', StringComparison.Ordinal)
                && string.Equals(CommonName(groupDn), configured.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>DN fără spații în jurul virgulelor, pentru comparație.</summary>
        private static string Canonical(string dn) =>
            string.Join(',', dn.Split(',').Select(part => part.Trim()));
    }
}
