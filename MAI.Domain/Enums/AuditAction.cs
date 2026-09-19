namespace MAI.Domain.Enums
{
    /// <summary>
    /// Tipul operației consemnate. Persistat ca int — membrii nu se reordonează
    /// și nu se șterg; unul scos din uz rămâne aici, marcat, ca rândurile vechi
    /// să rămână interpretabile.
    /// </summary>
    public enum AuditAction
    {
        Login = 0,
        Logout = 1,
        FileUpload = 2,
        FileDownload = 3,
        DocumentCreate = 4,
        DocumentNewVersion = 5,
        UserCreated = 6,
        UserUpdated = 7,

        /// <summary>Transfer șters (logic) de expeditor sau de administrator.</summary>
        FileDeleted = 8,

        TransferExpired = 9,

        /// <summary>Expeditorul a retras un transfer înainte de descărcare.</summary>
        TransferRevoked = 10,

        /// <summary>O sesiune a fost încheiată de la distanță, din pagina de sesiuni.</summary>
        SessionRevoked = 11,

        /// <summary>
        /// Un transfer a fost redirecționat către destinatari noi.
        ///
        /// Până acum forward-ul se consemna ca FileUpload, deci în jurnal nu se
        /// putea separa „a trimis un fișier nou” de „a dat mai departe un fișier
        /// primit” — exact întrebarea pe care o pune un control intern.
        /// </summary>
        TransferForwarded = 12,
    }
}
