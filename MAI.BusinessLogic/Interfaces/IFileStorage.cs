using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MAI.BusinessLogic.Interfaces
{
    /// <summary>
    /// Abstractizarea depozitului de fișiere.
    ///
    /// Restul aplicației nu știe unde ajung octeții: pe disc, în MinIO, în S3
    /// sau în Supabase Storage. Vorbește doar în termeni de "cheie de obiect" —
    /// niciodată cale absolută. Diferența nu e cosmetică: dacă în baza de date
    /// s-ar stoca o cale de sistem, orice bug care permite scrierea acelei
    /// coloane devine citire arbitrară de fișiere de pe server.
    ///
    /// Toate implementările primesc și returnează CIFROTEXT. Conținutul în clar
    /// nu ajunge niciodată aici — criptarea se face în browser, înainte de upload.
    /// </summary>
    public interface IFileStorage
    {
        /// <summary>Numele providerului, pentru loguri și pentru pagina de diagnostic.</summary>
        string ProviderName { get; }

        /// <summary>
        /// True dacă providerul poate emite URL-uri presemnate. Când e true,
        /// descărcarea ocolește API-ul și clientul ia octeții direct din depozit.
        /// </summary>
        bool SupportsPresignedUrls { get; }

        /// <summary>
        /// Scrie conținutul sub cheia dată. Suprascrie dacă cheia există deja.
        /// Stream-ul este consumat, nu bufferizat integral în memorie.
        /// </summary>
        /// <param name="contentLength">
        /// Dimensiunea exactă în octeți. Obligatorie: fără ea, SDK-ul S3 ar trebui
        /// să bufferizeze tot conținutul ca să calculeze Content-Length.
        /// </param>
        Task PutAsync(
            string key,
            Stream content,
            long contentLength,
            string contentType,
            CancellationToken ct = default);

        /// <summary>
        /// Deschide obiectul pentru citire. Apelantul închide stream-ul.
        /// Aruncă <see cref="FileNotFoundException"/> dacă cheia nu există.
        /// </summary>
        Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

        Task<bool> ExistsAsync(string key, CancellationToken ct = default);

        /// <summary>Șterge obiectul. Nu aruncă dacă cheia nu există (idempotent).</summary>
        Task DeleteAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// URL temporar de descărcare directă, semnat criptografic de server.
        /// Returnează null dacă providerul nu suportă (ex. filesystem local),
        /// caz în care apelantul trebuie să streameze conținutul prin API.
        ///
        /// Autorizarea se face la EMITEREA URL-ului, nu la folosirea lui: dacă
        /// utilizatorul nu are dreptul, controllerul nu semnează nimic.
        /// </summary>
        Task<string?> TryCreatePresignedDownloadUrlAsync(
            string key,
            TimeSpan lifetime,
            CancellationToken ct = default);

        /// <summary>
        /// Verifică dacă depozitul răspunde. Folosit de /api/health.
        ///
        /// Nu aruncă: returnează false. Un health check care propagă excepții
        /// obligă fiecare apelant să le prindă și, mai rău, riscă să scurgă
        /// endpointul și credențialele în mesajul erorii.
        ///
        /// Operația trebuie să fie ieftină și să nu scrie nimic — sonda rulează
        /// la fiecare câteva secunde, la infinit.
        /// </summary>
        Task<bool> HealthCheckAsync(CancellationToken ct = default);
    }
}