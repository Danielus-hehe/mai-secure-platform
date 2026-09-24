using System;

namespace MAI.BusinessLogic.Storage
{
    /// <summary>
    /// Configurarea depozitului de fișiere, secțiunea "Storage" din appsettings.json.
    /// </summary>
    public class StorageOptions
    {
        /// <summary>"S3" (MinIO, Amazon S3, R2, Supabase Storage) sau "Local".</summary>
        public string Provider { get; set; } = "Local";

        /// <summary>Endpointul S3. Pentru MinIO din docker-compose: http://localhost:9000</summary>
        public string Endpoint { get; set; } = "http://localhost:9000";

        public string AccessKey { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;

        /// <summary>
        /// Bucketul principal al aplicației.
        ///
        /// Valoarea implicită "mai-secure" este utilizată atât în docker-compose cât
        /// și în appsettings.json. Toate transferurile și documentele normative
        /// coexistă în același bucket:
        ///   • Transferuri:  {dept-slug}/{yyyy}/{MM}/{transferId:N}.enc
        ///   • Documente:    documents/{docId:N}/v{n}-{încercare:N}.{ext}
        ///                   (sufix unic per încărcare; cheile vechi: v{n}.{ext})
        ///
        /// Mediu de producție - dacă volumul o cere sau politicile IAM o impun, se
        /// pot folosi bucket-uri separate (ex. "mai-secure-transfers" și
        /// "mai-secure-documents") cu configurații Storage distincte per controller.
        /// </summary>
        public string Bucket { get; set; } = "mai-secure";

        /// <summary>MinIO ignoră regiunea, dar SDK-ul AWS o cere.</summary>
        public string Region { get; set; } = "us-east-1";

        /// <summary>
        /// True doar dacă endpointul este HTTPS. MinIO din compose rulează pe HTTP
        /// simplu, iar traficul nu părăsește mașina.
        /// </summary>
        public bool UseSsl { get; set; }

        /// <summary>
        /// Path-style (http://host/bucket/key) în loc de virtual-host-style
        /// (http://bucket.host/key). Obligatoriu pentru MinIO - nu are DNS wildcard.
        /// </summary>
        public bool ForcePathStyle { get; set; } = true;

        /// <summary>
        /// [Legacy - nefolosit pentru construcția cheilor noi]
        ///
        /// Înainte de Feature #4, toate transferurile aveau prefixul fix "transfers/".
        /// Acum prefixul de prim nivel este departamentul expeditorului (slug-ificat),
        /// calculat dinamic în TransfersController.BuildStorageKey.
        ///
        /// Proprietatea rămâne în clasă și în appsettings.json ca referință pentru
        /// consultanții care administrează instanțe vechi: bucket-urile create
        /// înainte de Feature #4 au obiectele sub "transfers/{yyyy}/{MM}/".
        /// Regulile ILM/lifecycle din MinIO care targetau "transfers/" nu se mai
        /// potrivesc cu cheile noi - a se vedea comentariile din docker-compose.yml.
        /// </summary>
        public string TransfersPrefix { get; set; } = "transfers";

        /// <summary>
        /// Când e true și providerul suportă, descărcarea se face direct din depozit
        /// cu URL presemnat: API-ul nu mai proxy-ază octeții. Pune false dacă la
        /// demonstrație apar probleme de CORS.
        /// </summary>
        public bool UsePresignedDownload { get; set; } = true;

        /// <summary>Cât timp rămâne valabil un URL presemnat.</summary>
        public int PresignedUrlMinutes { get; set; } = 5;

        /// <summary>Limita per fișier, în MB. Se aplică la cifrotext.</summary>
        public int MaxFileSizeMb { get; set; } = 50;

        /// <summary>Doar pentru providerul Local: folderul rădăcină.</summary>
        public string LocalRootPath { get; set; } = "Storage";

        public long MaxFileSizeBytes => (long)MaxFileSizeMb * 1024 * 1024;

        public TimeSpan PresignedUrlLifetime => TimeSpan.FromMinutes(
            PresignedUrlMinutes < 1 ? 1 : PresignedUrlMinutes);

        /// <summary>
        /// Validare la pornire. Mai bine crapă procesul acum, cu un mesaj clar,
        /// decât la primul upload al utilizatorului.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Provider))
                throw new InvalidOperationException("Storage:Provider lipsește.");

            if (!IsS3 && !IsLocal)
                throw new InvalidOperationException(
                    $"Storage:Provider = '{Provider}' necunoscut. Valori acceptate: S3, Local.");

            if (MaxFileSizeMb < 1)
                throw new InvalidOperationException("Storage:MaxFileSizeMb trebuie să fie cel puțin 1.");

            if (!IsS3) return;

            if (string.IsNullOrWhiteSpace(Endpoint))
                throw new InvalidOperationException("Storage:Endpoint lipsește.");

            if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
                throw new InvalidOperationException(
                    "Storage:AccessKey / Storage:SecretKey lipsesc. Setează-le prin " +
                    "variabilele de mediu MAI_STORAGE_ACCESS_KEY și MAI_STORAGE_SECRET_KEY.");

            if (string.IsNullOrWhiteSpace(Bucket))
                throw new InvalidOperationException("Storage:Bucket lipsește.");

            if (UseSsl && Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Storage:UseSsl = true dar endpointul este http://. Configurație contradictorie.");
        }

        public bool IsS3 => string.Equals(Provider, "S3", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Provider, "MinIO", StringComparison.OrdinalIgnoreCase);

        public bool IsLocal => string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase);
    }
}
