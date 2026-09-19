using MAI.Domain.Entities;
using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Transfers
{
    /// <summary>
    /// Regulile de stare ale unui transfer, ca funcții pure.
    ///
    /// Stau aici, nu în controller, din două motive: sunt folosite din mai multe
    /// locuri (TransfersController, StatsController, jobul de expirare) și se pot
    /// testa fără bază de date. Înainte, „este expirat?” era scris de mână în
    /// patru locuri, iar variantele nu erau identice.
    /// </summary>
    public static class TransferRules
    {
        /// <summary>
        /// Starea agregată după confirmări: Downloaded doar dacă TOȚI destinatarii
        /// au descărcat. Stările terminale (Expired, Revoked) nu se schimbă.
        /// </summary>
        public static TransferStatus AggregateStatus(
            TransferStatus current, IEnumerable<TransferRecipient> recipients)
        {
            if (current is TransferStatus.Expired or TransferStatus.Revoked)
                return current;

            var list = recipients as ICollection<TransferRecipient> ?? recipients.ToList();

            return list.Count > 0 && list.All(r => r.DownloadedAt.HasValue)
                ? TransferStatus.Downloaded
                : TransferStatus.Pending;
        }

        /// <summary>
        /// True dacă termenul a trecut. Jobul de expirare rulează periodic, deci
        /// între ExpiresAt și marcarea efectivă e o fereastră în care Status e încă
        /// Pending; accesul se refuză pe baza datei, nu a statusului.
        /// </summary>
        public static bool IsPastExpiry(FileTransfer t, DateTime nowUtc) =>
            t.ExpiresAt.HasValue && t.ExpiresAt.Value <= nowUtc;

        /// <summary>
        /// Conținutul mai poate fi descărcat: nu e șters, retras sau expirat.
        /// Un transfer Downloaded rămâne descărcabil până la termen - și pentru
        /// destinatarii care îl mai deschid o dată, și pentru expeditor.
        /// </summary>
        public static bool IsContentAvailable(FileTransfer t, DateTime nowUtc) =>
            t.DeletedAt is null
            && t.Status is TransferStatus.Pending or TransferStatus.Downloaded
            && !IsPastExpiry(t, nowUtc);

        /// <summary>
        /// Starea afișată: un transfer în așteptare trecut de termen apare ca
        /// Expired chiar dacă jobul nu l-a marcat încă. Downloaded rămâne
        /// Downloaded - este o dovadă, nu o stare de curățenie.
        /// </summary>
        public static TransferStatus EffectiveStatus(FileTransfer t, DateTime nowUtc) =>
            t.Status == TransferStatus.Pending && IsPastExpiry(t, nowUtc)
                ? TransferStatus.Expired
                : t.Status;

        /// <summary>
        /// Poate utilizatorul să adauge destinatari? Expeditorul - întotdeauna,
        /// cât timp conținutul există. Un destinatar - doar dacă expeditorul a
        /// permis redistribuirea.
        /// </summary>
        public static bool CanForward(FileTransfer t, Guid userId, bool isRecipient, DateTime nowUtc)
        {
            if (!IsContentAvailable(t, nowUtc)) return false;
            if (t.SenderId == userId) return true;
            return isRecipient && t.AllowForward;
        }

        /// <summary>
        /// Retragerea are sens cât timp există cineva care nu a descărcat încă.
        /// Cei care au descărcat deja păstrează fișierul; retragerea le blochează
        /// doar pe ceilalți.
        /// </summary>
        public static bool CanRevoke(FileTransfer t, Guid userId, DateTime nowUtc) =>
            t.SenderId == userId
            && t.DeletedAt is null
            && t.Status == TransferStatus.Pending
            && !IsPastExpiry(t, nowUtc);

        /// <summary>
        /// Rezolvă data de expirare cerută de expeditor.
        ///
        /// Null → valoarea implicită din politică. Data se normalizează la UTC:
        /// o valoare fără fus orar (Kind=Unspecified) e tratată ca UTC, nu ca ora
        /// locală a serverului - în container, ora locală e oricum UTC, iar pe
        /// Windows ar fi fost ora Chișinăului, deci același request ar fi dat
        /// rezultate diferite în funcție de unde rulează API-ul.
        /// </summary>
        public static ExpiryResult ResolveExpiry(DateTime? requested, TransferPolicyOptions policy, DateTime nowUtc)
        {
            if (requested is null)
                return ExpiryResult.Ok(nowUtc.AddDays(policy.DefaultExpiryDays));

            var value = requested.Value.Kind switch
            {
                DateTimeKind.Utc         => requested.Value,
                DateTimeKind.Local       => requested.Value.ToUniversalTime(),
                _                        => DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc),
            };

            if (value <= nowUtc)
                return ExpiryResult.Fail("Data de expirare nu poate fi în trecut.");

            // Un minut de toleranță: formularul calculează maximul în browser, iar
            // ceasul clientului și al serverului nu sunt identice.
            var max = nowUtc.AddDays(policy.MaxExpiryDays).AddMinutes(1);
            if (value > max)
                return ExpiryResult.Fail(
                    $"Valabilitatea maximă a unui transfer este de {policy.MaxExpiryDays} zile.");

            return ExpiryResult.Ok(value);
        }

        /// <summary>Validează o categorie primită din exterior (formular, JSON).</summary>
        public static bool IsValidCategory(TransferCategory category) =>
            Enum.IsDefined(typeof(TransferCategory), category);
    }

    public readonly record struct ExpiryResult(bool Success, DateTime Value, string? Error)
    {
        public static ExpiryResult Ok(DateTime value) => new(true, value, null);
        public static ExpiryResult Fail(string error) => new(false, default, error);
    }
}
