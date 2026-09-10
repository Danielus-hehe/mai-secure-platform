namespace MAI.Domain.Enums
{
    /// <summary>
    /// Starea unui transfer.
    ///
    /// Valorile sunt persistate ca int, deci ordinea membrilor nu se schimbă:
    /// o reordonare ar reinterpreta transferurile deja existente.
    /// </summary>
    public enum TransferStatus
    {
        /// <summary>Încărcat, încă nedescărcat de destinatar.</summary>
        Pending = 0,

        /// <summary>Destinatarul l-a descărcat și decriptat.</summary>
        Downloaded = 1,

        /// <summary>A trecut de ExpiresAt; obiectul din depozit a fost șters.</summary>
        Expired = 2,

        /// <summary>
        /// Expeditorul l-a retras înainte de a fi descărcat.
        ///
        /// Stare distinctă de Expired, deși ambele înseamnă „obiectul nu mai
        /// există”: expirarea e automată și așteptată, retragerea e o decizie
        /// umană care trebuie să rămână vizibilă în jurnal. Un supervizor care
        /// vede că un document a fost trimis și retras după zece minute are altă
        /// informație decât dacă ar vedea doar că a expirat.
        ///
        /// Rândul NU se șterge: destinatarul trebuie să vadă că i s-a trimis
        /// ceva și că a fost retras, altfel transferul dispare din interfața lui
        /// fără explicație.
        /// </summary>
        Revoked = 3,
    }
}
