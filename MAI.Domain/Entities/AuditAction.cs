namespace MAI.Domain.Enums
{
    /// <summary>
    /// Acțiunile înregistrate în jurnalul de audit.
    ///
    /// IMPORTANT: valorile se stochează în baza de date ca întregi (ordinea din
    /// enum). Membrii noi se adaugă DOAR la sfârșit — o inserare la mijloc ar
    /// reinterpreta retroactiv toate înregistrările existente, adică ar falsifica
    /// jurnalul de audit.
    /// </summary>
    public enum AuditAction
    {
        Login,
        Logout,
        FileUpload,
        FileDownload,
        DocumentCreate,
        DocumentNewVersion,
        UserCreated,
        UserUpdated,

        /// <summary>Ștergerea unui transfer de către expeditor sau administrator.</summary>
        FileDeleted,
    }
}