using System;
using System.Threading;
using System.Threading.Tasks;

namespace MAI.BusinessLogic.Interfaces
{
    /// <summary>Rezultatul verificării unei parole.</summary>
    public enum PasswordVerificationResult
    {
        /// <summary>Parola nu corespunde.</summary>
        Failed = 0,

        /// <summary>Parola corespunde și hash-ul folosește parametrii curenți.</summary>
        Success = 1,

        /// <summary>
        /// Parola corespunde, dar hash-ul stocat este vechi (plain text legacy sau
        /// parametri Argon2 depășiți). Apelantul trebuie să re-hash-uiască și să salveze.
        /// </summary>
        SuccessRehashNeeded = 2,
    }

    /// <summary>
    /// Aruncată când toate sloturile de hashing sunt ocupate și cererea a stat în coadă
    /// mai mult decât QueueTimeoutSeconds. Se traduce în HTTP 503 + Retry-After.
    /// Este backpressure intenționat: mai bine refuzăm cereri decât să rămânem fără memorie.
    /// </summary>
    public class HashingCapacityExceededException : Exception
    {
        public HashingCapacityExceededException(string message) : base(message) { }
    }

    public interface IPasswordHasher
    {
        /// <summary>
        /// Produce un hash Argon2id în format PHC:
        /// $argon2id$v=19$m=19456,t=2,p=1$&lt;salt-b64&gt;$&lt;hash-b64&gt;
        /// </summary>
        /// <param name="profileName">
        /// Numele profilului de cost. Null = profilul implicit. Folosește profilul
        /// privilegiat pentru conturi de administrator și resetări administrative.
        /// </param>
        Task<string> HashPasswordAsync(string password, string? profileName = null, CancellationToken ct = default);

        /// <summary>
        /// Verifică parola în timp constant față de hash-ul stocat, folosind parametrii
        /// codificați în hash. Suportă și hash-uri legacy în plain text (migrare).
        /// </summary>
        Task<PasswordVerificationResult> VerifyPasswordAsync(
            string password, string? storedHash, CancellationToken ct = default);

        /// <summary>
        /// Consumă același timp de calcul ca o verificare reală, fără a compara nimic.
        /// Se apelează când utilizatorul nu există, ca să nu se poată enumera conturi
        /// prin măsurarea timpului de răspuns.
        /// </summary>
        Task SimulateVerificationAsync(CancellationToken ct = default);

        /// <summary>Câte sloturi de hashing sunt libere acum. Pentru diagnostic / health check.</summary>
        int AvailableCapacity { get; }

        /// <summary>Consumul maxim teoretic de memorie pentru hashing, în MiB.</summary>
        int PeakMemoryMib { get; }
    }
}