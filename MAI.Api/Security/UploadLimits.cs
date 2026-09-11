namespace MAI.Api.Security
{
    /// <summary>
    /// Limitele corpului cererilor HTTP, într-un singur loc.
    ///
    /// Atributele C# cer constante, deci limita de upload nu poate fi citită
    /// direct din Storage:MaxFileSizeMb. Valoarea de aici e plafonul de transport;
    /// limita reală a fișierului (Storage:MaxFileSizeMb) se verifică în
    /// controller, cu un mesaj clar pentru utilizator.
    /// </summary>
    public static class UploadLimits
    {
        /// <summary>
        /// Plafonul pentru endpointurile multipart (transferuri, documente):
        /// 50 MB de fișier plus 1 MB pentru antetele multipart și câmpurile
        /// plicului criptografic. Fără marja de 1 MB, un fișier de exact 50 MB
        /// era respins de server înainte să ajungă la controller.
        /// </summary>
        public const long MaxRequestBytes = 51L * 1024 * 1024;

        /// <summary>
        /// Plafonul global, pentru toate celelalte cereri. Cel mai mare corp JSON
        /// legitim (pachetul de chei publice și blobul cheilor private) are câțiva
        /// kiloocteți; 1 MB lasă o marjă generoasă.
        /// </summary>
        public const long MaxJsonRequestBytes = 1L * 1024 * 1024;
    }
}
