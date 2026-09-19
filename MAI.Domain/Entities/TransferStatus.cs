namespace MAI.Domain.Enums
{
    /// <summary>
    /// Starea unui transfer, la nivelul întregului transfer.
    ///
    /// Starea fiecărui destinatar (a descărcat sau nu, semnătură validă sau nu)
    /// stă pe TransferRecipient. Aici e doar agregatul - ce vede expeditorul
    /// dintr-o privire în listă.
    ///
    /// Valorile sunt persistate ca int, deci ordinea membrilor nu se schimbă:
    /// o reordonare ar reinterpreta transferurile deja existente.
    /// </summary>
    public enum TransferStatus
    {
        /// <summary>Activ: cel puțin un destinatar nu a descărcat încă fișierul.</summary>
        Pending = 0,

        /// <summary>
        /// TOȚI destinatarii au descărcat și decriptat fișierul.
        ///
        /// Înainte de dovada de primire per destinatar, prima confirmare punea
        /// transferul în starea asta, oricâți destinatari ar fi avut. Acum starea
        /// se recalculează la fiecare confirmare și la fiecare forward: un
        /// destinatar nou adăugat readuce transferul în Pending.
        /// </summary>
        Downloaded = 1,

        /// <summary>
        /// A trecut de ExpiresAt înainte ca toți destinatarii să-l descarce;
        /// obiectul din depozit a fost șters.
        ///
        /// Un transfer Downloaded care trece de termen rămâne Downloaded (obiectul
        /// se șterge oricum): starea „primit de toți” e o dovadă și nu are voie
        /// să fie suprascrisă de un job de curățenie.
        /// </summary>
        Expired = 2,

        /// <summary>
        /// Expeditorul l-a retras înainte ca toți destinatarii să-l descarce.
        ///
        /// Stare distinctă de Expired, deși ambele înseamnă „obiectul nu mai
        /// există”: expirarea e automată și așteptată, retragerea e o decizie
        /// umană care trebuie să rămână vizibilă în jurnal.
        ///
        /// Rândul NU se șterge: destinatarul trebuie să vadă că i s-a trimis
        /// ceva și că a fost retras, altfel transferul dispare din interfața lui
        /// fără explicație.
        /// </summary>
        Revoked = 3,
    }
}
