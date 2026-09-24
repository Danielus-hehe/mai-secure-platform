using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MAI.DataAccessLayer
{
    /// <summary>
    /// Aduce rândurile noi de audit în limitele coloanelor, înainte de salvare.
    ///
    /// De ce aici și nu în fiecare controller: textul din AuditLogs.Details se
    /// construiește în aproape treizeci de locuri, din date controlate de
    /// utilizator (nume de fișier de până la 260 de caractere, până la 20 de
    /// destinatari cu numele complet, liste de modificări venite din AD). La
    /// trimiterea unui fișier către mulți destinatari textul depășea ușor 1024
    /// de caractere: SaveChanges arunca, obiectul abia scris în MinIO era șters
    /// ca eșec, iar utilizatorul primea 500 pentru o operație perfect legitimă.
    /// Un rând de jurnal prea lung nu are voie să anuleze operația pe care o
    /// consemnează.
    ///
    /// Limitele se citesc din modelul EF (HasMaxLength din
    /// AuditLogConfiguration), nu sunt copiate aici: o coloană lărgită printr-o
    /// migrare e respectată automat.
    ///
    /// Se aplică DOAR jurnalului de audit. Pentru celelalte tabele, o valoare
    /// prea lungă e o eroare de validare care trebuie să ajungă la utilizator,
    /// nu să fie tăiată pe tăcute.
    /// </summary>
    public static class AuditLogLimits
    {
        /// <summary>Marcajul de la finalul unui text tăiat: cititorul jurnalului vede că lipsește ceva.</summary>
        public const string TruncationMarker = "…";

        /// <summary>Trunchiază câmpurile text ale rândurilor de audit adăugate.</summary>
        public static void Apply(ChangeTracker changeTracker)
        {
            foreach (var entry in changeTracker.Entries<AuditLog>())
            {
                if (entry.State != EntityState.Added) continue;

                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.ClrType != typeof(string)) continue;

                    var max = property.Metadata.GetMaxLength();
                    if (max is null) continue;

                    if (property.CurrentValue is string value)
                        property.CurrentValue = Fit(value, max.Value);
                }
            }
        }

        /// <summary>
        /// Textul, tăiat la <paramref name="maxLength"/> caractere cu tot cu
        /// marcaj. Nu rupe o pereche surogat (emoji, caractere în afara BMP):
        /// jumătatea rămasă ar fi un caracter invalid pe care PostgreSQL îl
        /// refuză în UTF-8.
        /// </summary>
        public static string Fit(string value, int maxLength)
        {
            if (value.Length <= maxLength) return value;
            if (maxLength <= TruncationMarker.Length) return value[..maxLength];

            var cut = maxLength - TruncationMarker.Length;
            if (char.IsHighSurrogate(value[cut - 1])) cut--;

            return value[..cut] + TruncationMarker;
        }
    }
}
