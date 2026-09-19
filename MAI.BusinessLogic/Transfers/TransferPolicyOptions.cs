namespace MAI.BusinessLogic.Transfers
{
    /// <summary>
    /// Politica transferurilor, din secțiunea „Transfers” (appsettings.json)
    /// sau din .env: TRANSFER_DEFAULT_EXPIRY_DAYS, TRANSFER_MAX_EXPIRY_DAYS,
    /// TRANSFER_MAX_RECIPIENTS.
    ///
    /// Aceleași valori ajung în browser prin GET /api/Transfers/policy, ca
    /// formularul de trimitere să nu le dubleze într-o constantă proprie care
    /// s-ar desincroniza de server.
    /// </summary>
    public class TransferPolicyOptions
    {
        /// <summary>Valabilitatea aplicată când expeditorul nu alege o dată.</summary>
        public int DefaultExpiryDays { get; set; } = 7;

        /// <summary>
        /// Cea mai lungă valabilitate permisă. Fără limită, un expeditor putea
        /// cere expirare peste 50 de ani — adică un document sensibil ținut pe
        /// server pe termen nedefinit, exact ce expirarea trebuia să prevină.
        /// </summary>
        public int MaxExpiryDays { get; set; } = 30;

        /// <summary>
        /// Numărul maxim de destinatari ai unui transfer (direcți + forward).
        /// Fiecare destinatar costă o împachetare RSA în browser și un rând în bază.
        /// </summary>
        public int MaxRecipients { get; set; } = 20;

        public void Validate()
        {
            if (DefaultExpiryDays <= 0)
                throw new InvalidOperationException("Transfers:DefaultExpiryDays trebuie să fie > 0.");

            if (MaxExpiryDays <= 0 || MaxExpiryDays > 365)
                throw new InvalidOperationException("Transfers:MaxExpiryDays trebuie să fie între 1 și 365.");

            if (DefaultExpiryDays > MaxExpiryDays)
                throw new InvalidOperationException(
                    $"Transfers:DefaultExpiryDays ({DefaultExpiryDays}) nu poate depăși " +
                    $"Transfers:MaxExpiryDays ({MaxExpiryDays}).");

            if (MaxRecipients is <= 0 or > 100)
                throw new InvalidOperationException("Transfers:MaxRecipients trebuie să fie între 1 și 100.");
        }
    }
}
