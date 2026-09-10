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
        FileDeleted = 8,
        TransferExpired = 9,

        /// <summary>Expeditorul a retras un transfer înainte de descărcare.</summary>
        TransferRevoked = 10,

        /// <summary>O sesiune a fost încheiată de la distanță, din pagina de sesiuni.</summary>
        SessionRevoked = 11,
    }
}
