using System;
using System.Collections.Generic;

namespace MAI.BusinessLogic.Security
{
    /// <summary>Un set de parametri de cost Argon2id.</summary>
    public class Argon2Profile
    {
        /// <summary>Memorie folosită, în KiB.</summary>
        public int MemorySizeKib { get; set; } = 19456;

        /// <summary>Numărul de iterații (time cost).</summary>
        public int Iterations { get; set; } = 2;

        /// <summary>Gradul de paralelism (fire per hash).</summary>
        public int DegreeOfParallelism { get; set; } = 1;

        public int SaltSizeBytes { get; set; } = 16;
        public int HashSizeBytes { get; set; } = 32;

        public void Validate(string name)
        {
            if (MemorySizeKib < 8192)
                throw new InvalidOperationException($"Argon2 profil '{name}': MemorySizeKib minim 8192 (8 MiB).");
            if (Iterations < 1)
                throw new InvalidOperationException($"Argon2 profil '{name}': Iterations minim 1.");
            if (DegreeOfParallelism < 1)
                throw new InvalidOperationException($"Argon2 profil '{name}': DegreeOfParallelism minim 1.");
            if (SaltSizeBytes < 8)
                throw new InvalidOperationException($"Argon2 profil '{name}': SaltSizeBytes minim 8.");
            if (HashSizeBytes < 16)
                throw new InvalidOperationException($"Argon2 profil '{name}': HashSizeBytes minim 16.");
        }
    }

    /// <summary>
    /// Configurarea hashing-ului de parole.
    ///
    /// ASYMMETRIC TUNING — nu toate operațiile costă la fel de mult.
    /// Login-ul se întâmplă des și trebuie să reziste la concurență, deci folosește
    /// profilul "Interactive". Crearea de cont, resetarea de parolă și conturile
    /// privilegiate se întâmplă rar, deci pot plăti un cost mai mare ("Sensitive").
    /// Parametrii sunt scriși în însuși string-ul PHC, deci verificarea folosește automat
    /// costul cu care a fost creat hash-ul respectiv.
    ///
    /// OFFLOADING / BACKPRESSURE — MaxConcurrentHashes limitează câte operații Argon2id
    /// rulează simultan. Consumul maxim de memorie devine determinist:
    ///     MaxConcurrentHashes x MemorySizeKib al celui mai scump profil.
    /// Peste limită, cererile așteaptă în coadă; dacă depășesc QueueTimeoutSeconds,
    /// primesc 503 în loc să scoată serverul din memorie.
    /// </summary>
    public class Argon2Options
    {
        /// <summary>Profilele disponibile, pe nume. Se pot defini oricâte în appsettings.</summary>
        public Dictionary<string, Argon2Profile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            // OWASP Password Storage Cheat Sheet — minim recomandat pentru Argon2id.
            ["Interactive"] = new Argon2Profile
            {
                MemorySizeKib = 19456, Iterations = 2, DegreeOfParallelism = 1,
            },
            // Cost mai mare pentru operații rare și conturi privilegiate.
            ["Sensitive"] = new Argon2Profile
            {
                MemorySizeKib = 65536, Iterations = 3, DegreeOfParallelism = 1,
            },
        };

        /// <summary>Profilul folosit implicit (conturi obișnuite).</summary>
        public string DefaultProfile { get; set; } = "Interactive";

        /// <summary>Profilul folosit pentru conturi privilegiate și resetări administrative.</summary>
        public string PrivilegedProfile { get; set; } = "Sensitive";

        /// <summary>
        /// Câte hash-uri Argon2id pot rula simultan. Aceasta este limita reală de memorie.
        /// Regulă practică: (RAM disponibil / 2) împărțit la memoria profilului scump.
        /// </summary>
        public int MaxConcurrentHashes { get; set; } = 4;

        /// <summary>Cât așteaptă o cerere în coadă înainte să primească 503.</summary>
        public int QueueTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// "Pepper" — secret global păstrat în afara bazei de date.
        /// ATENȚIE: dacă îl schimbi, toate parolele existente devin invalide.
        /// </summary>
        public string? Pepper { get; set; }

        /// <summary>
        /// Acceptă la login parole stocate în clar (starea inițială a bazei) și le migrează.
        /// Se pune pe false după încheierea migrării.
        /// </summary>
        public bool AllowLegacyPlaintext { get; set; } = true;

        public Argon2Profile GetProfile(string? name)
        {
            var key = string.IsNullOrWhiteSpace(name) ? DefaultProfile : name;

            if (Profiles.TryGetValue(key, out var profile))
                return profile;

            if (Profiles.TryGetValue(DefaultProfile, out var fallback))
                return fallback;

            throw new InvalidOperationException(
                $"Profilul Argon2 '{key}' nu există și nici profilul implicit '{DefaultProfile}'.");
        }

        /// <summary>Consumul maxim teoretic de memorie pentru hashing, în MiB.</summary>
        public int EstimatedPeakMemoryMib
        {
            get
            {
                var maxKib = 0;
                foreach (var p in Profiles.Values)
                    if (p.MemorySizeKib > maxKib) maxKib = p.MemorySizeKib;
                return (int)Math.Ceiling(maxKib * (long)MaxConcurrentHashes / 1024.0);
            }
        }

        public void Validate()
        {
            if (Profiles.Count == 0)
                throw new InvalidOperationException("Argon2: nu este definit niciun profil.");

            foreach (var kv in Profiles)
                kv.Value.Validate(kv.Key);

            if (!Profiles.ContainsKey(DefaultProfile))
                throw new InvalidOperationException($"Argon2:DefaultProfile '{DefaultProfile}' nu există în Profiles.");
            if (!Profiles.ContainsKey(PrivilegedProfile))
                throw new InvalidOperationException($"Argon2:PrivilegedProfile '{PrivilegedProfile}' nu există în Profiles.");

            if (MaxConcurrentHashes < 1)
                throw new InvalidOperationException("Argon2:MaxConcurrentHashes trebuie să fie minim 1.");
            if (QueueTimeoutSeconds < 1)
                throw new InvalidOperationException("Argon2:QueueTimeoutSeconds trebuie să fie minim 1.");
        }
    }
}