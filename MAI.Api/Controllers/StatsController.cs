using System.Diagnostics;
using System.Security.Claims;
using MAI.Api.BackgroundJobs;
using MAI.Api.Services;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage;
using MAI.BusinessLogic.Storage.Encryption;
using MAI.BusinessLogic.Transfers;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{

    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class StatsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly TransferExpirationState _expirationState;
        private readonly TransferExpirationOptions _expirationOptions;
        private readonly ILogger<StatsController> _logger;

        public StatsController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            TransferExpirationState expirationState,
            TransferExpirationOptions expirationOptions,
            ILogger<StatsController> logger)
        {
            _context           = context;
            _storage           = storage;
            _storageOptions    = storageOptions;
            _expirationState   = expirationState;
            _expirationOptions = expirationOptions;
            _logger            = logger;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Stats - pagina principală (orice utilizator autentificat)
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Cifrele paginii principale, din perspectiva utilizatorului curent.
        ///
        /// Transferurile sunt STRICT ale lui: trimise sau primite de el. Numele
        /// fișierelor și perechile expeditor–destinatar sunt metadate sensibile.
        /// O listă comună, vizibilă oricărui cont, ar arăta pe prima pagină cine
        /// ce trimite cui în toată instituția. Cifrele globale despre transferuri
        /// stau pe /admin, doar pentru Administrator.
        ///
        /// Documentele normative și numărul de utilizatori activi rămân globale:
        /// registrul e public în instituție, iar un număr de colegi nu identifică
        /// pe nimeni.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStats(CancellationToken ct)
        {
            var userId    = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var now       = DateTime.UtcNow;
            var yesterday = now.AddHours(-24);

            var mine = _context.FileTransfers
                .AsNoTracking()
                .Where(t => t.DeletedAt == null)
                .Where(t => t.SenderId == userId || t.Recipients.Any(r => r.UserId == userId));

            var myTransfersTotal = await mine.CountAsync(ct);

            // Fișierele care îl așteaptă pe utilizator: rândul LUI de destinatar
            // e neconfirmat, iar transferul e activ și în termen. Starea
            // agregată nu ajunge: un transfer rămâne Pending cât timp ORICE
            // destinatar nu l-a descărcat, deci ar fi numărat și la cei care
            // l-au descărcat deja.
            var awaitingMyDownload = await _context.TransferRecipients.CountAsync(r =>
                r.UserId == userId &&
                r.DownloadedAt == null &&
                r.Transfer!.DeletedAt == null &&
                r.Transfer.Status == TransferStatus.Pending &&
                (r.Transfer.ExpiresAt == null || r.Transfer.ExpiresAt > now), ct);

            var activeUsers    = await _context.Users.CountAsync(u => u.IsActive, ct);
            var totalDocuments = await _context.Documents.CountAsync(ct);

            // Doar pentru rolurile care au dreptul să o vadă. Pentru ceilalți câmpul
            // e null, nu 0: un zero ar afirma „nicio autentificare eșuată”, adică o
            // informație falsă, afișată ca atare.
            var canSeeSecurityMetrics =
                User.IsInRole(nameof(UserRole.Administrator)) ||
                User.IsInRole(nameof(UserRole.SefDirectie));

            int? failedLoginsLast24h = canSeeSecurityMetrics
                ? await _context.AuditLogs.CountAsync(a =>
                    a.Action == AuditAction.Login &&
                    a.Result == AuditResult.Failure &&
                    a.Timestamp >= yesterday, ct)
                : null;

            var recent = await mine
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .Include(t => t.Sender)
                .Include(t => t.Recipients).ThenInclude(r => r.User)
                .AsSplitQuery()
                .ToListAsync(ct);

            var recentTransfers = recent.Select(t => new
            {
                id            = t.Id,
                fileName      = t.FileName,
                fileSize      = t.FileSize,
                direction     = t.SenderId == userId ? "sent" : "received",
                senderName    = DisplayName(t.Sender),
                recipientName = RecipientsLabel(t.Recipients),
                // Aceeași regulă ca lista de transferuri: un transfer în așteptare
                // trecut de termen apare ca expirat, chiar dacă jobul nu l-a
                // marcat încă.
                status        = TransferRules.EffectiveStatus(t, now).ToString(),
                createdAt     = t.CreatedAt,
            }).ToList();

            return Ok(new
            {
                activeUsers,
                totalDocuments,
                myTransfersTotal,
                awaitingMyDownload,
                failedLoginsLast24h,
                recentTransfers,
            });
        }

        /// <summary>Numele afișat al unui cont: numele complet, altfel username-ul.</summary>
        private static string DisplayName(User? user) =>
            user is null
                ? "-"
                : string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;

        /// <summary>„Ion Popescu”, „Ion Popescu, Ana Rusu” sau „Ion Popescu +3”.</summary>
        private static string RecipientsLabel(IEnumerable<TransferRecipient> recipients)
        {
            var names = recipients
                .OrderBy(r => r.SentAt)
                .Select(r => DisplayName(r.User))
                .ToList();

            return names.Count switch
            {
                0 => "-",
                1 => names[0],
                2 => $"{names[0]}, {names[1]}",
                _ => $"{names[0]} +{names.Count - 1}",
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Stats/alerts - semnale de securitate (SefDirectie + Administrator)
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Transformă jurnalul de audit din arhivă pasivă în instrument de
        /// supraveghere: în loc să ceară cuiva să citească mii de rânduri, scoate
        /// în față tiparele care merită atenție acum.
        ///
        /// Toate interogările de aici se sprijină pe indexul compus
        /// Action + Result + Timestamp, adăugat odată cu migrarea rezultatului pe
        /// coloană. Fără el, pagina ar face scan complet la fiecare încărcare.
        /// </summary>
        [Authorize(Roles = "Administrator,SefDirectie")]
        [HttpGet("alerts")]
        public async Task<IActionResult> GetAlerts(CancellationToken ct)
        {
            var now       = DateTime.UtcNow;
            var last24h   = now.AddHours(-24);
            var last7d    = now.AddDays(-7);

            var alerts = new List<SecurityAlert>();

            // Aceeași regulă ca jurnalul: un șef de direcție vede semnalele
            // subdiviziunii lui, nu ale ministerului (null = administrator, tot).
            // Fără ea, panoul de alerte ar fi fost o scurgere a exact ce jurnalul
            // filtrează: cine a greșit parola, cine s-a autentificat cu cod de
            // recuperare, ce conturi privilegiate nu au 2FA.
            var visible = await OrgStructure.AuditVisibleUsersAsync(_context, User, ct);

            IQueryable<AuditLog> Logs() => visible is null
                ? _context.AuditLogs
                : _context.AuditLogs.Where(a => a.UserId != null && visible.Contains(a.UserId.Value));

            IQueryable<User> People() => visible is null
                ? _context.Users
                : _context.Users.Where(u => visible.Contains(u.Id));

            // ── 1. Conturi cu multe esecuri de autentificare ─────────────────
            // Gruparea se face in baza de date, nu prin aducerea randurilor in
            // memorie: pe un jurnal mare, diferenta e intre milisecunde si secunde.
            var bruteForce = await Logs()
                .Where(a => a.Action == AuditAction.Login
                         && a.Result == AuditResult.Failure
                         && a.Timestamp >= last24h)
                .GroupBy(a => a.Username)
                .Select(g => new { Username = g.Key, Count = g.Count() })
                .Where(x => x.Count >= FailedLoginAlertThreshold)
                .OrderByDescending(x => x.Count)
                .Take(MaxAlertsPerCategory)
                .ToListAsync(ct);

            foreach (var item in bruteForce)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "high",
                    Category: "Autentificare",
                    Title:    $"{item.Count} încercări eșuate pentru @{item.Username}",
                    Detail:   "În ultimele 24 de ore. Verificați dacă este o parolă uitată " +
                               "sau o încercare de forțare a contului.",
                    Username: item.Username));
            }

            // ── 2. Autentificari cu cod de recuperare ────────────────────────
            // Un cod de recuperare inseamna ca utilizatorul nu mai are acces la
            // aplicatia de autentificare. Legitim de cele mai multe ori, dar e si
            // exact calea pe care ar veni cineva care a obtinut parola si codurile
            // scrise pe hartie.
            var recoveryLogins = await Logs()
                .Where(a => a.Action == AuditAction.Login
                         && a.Result == AuditResult.Warning
                         && a.Timestamp >= last7d
                         && a.Details.Contains("COD DE RECUPERARE"))
                .OrderByDescending(a => a.Timestamp)
                .Take(MaxAlertsPerCategory)
                .Select(a => new { a.Username, a.Timestamp, a.IpAddress })
                .ToListAsync(ct);

            foreach (var item in recoveryLogins)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "medium",
                    Category: "2FA",
                    Title:    $"@{item.Username} s-a autentificat cu un cod de recuperare",
                    Detail:   $"{item.Timestamp:dd.MM.yyyy HH:mm} UTC, de la {item.IpAddress}. " +
                               "Confirmați că utilizatorul și-a reînrolat aplicația de autentificare.",
                    Username: item.Username));
            }

            // ── 3. Semnaturi invalide la descarcare ──────────────────────────
            // Cel mai grav semnal din lista: inseamna ca un fisier a ajuns la
            // destinatar fara sa se poata dovedi cine l-a trimis.
            var badSignatures = await Logs()
                .Where(a => a.Action == AuditAction.FileDownload
                         && a.Result == AuditResult.Warning
                         && a.Timestamp >= last7d
                         && a.Details.Contains("INVALIDA"))
                .OrderByDescending(a => a.Timestamp)
                .Take(MaxAlertsPerCategory)
                .Select(a => new { a.Username, a.Timestamp, a.Details })
                .ToListAsync(ct);

            foreach (var item in badSignatures)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "high",
                    Category: "Integritate",
                    Title:    $"Semnătură invalidă la o descărcare a lui @{item.Username}",
                    Detail:   $"{item.Timestamp:dd.MM.yyyy HH:mm} UTC. Fișierul a ajuns la destinatar, " +
                               "dar autenticitatea expeditorului NU a putut fi dovedită.",
                    Username: item.Username));
            }

            // ── 4. Conturi privilegiate fara 2FA ─────────────────────────────
            // Nu vine din jurnal, ci din starea curenta. Un administrator fara al
            // doilea factor face din parola lui singurul lucru care sta intre un
            // atacator si intregul sistem.
            var privilegedWithout2Fa = await People()
                .Where(u => u.IsActive
                         && u.Role >= UserRole.SefDirectie
                         && !u.TwoFactorEnabled)
                .OrderBy(u => u.Username)
                .Take(MaxAlertsPerCategory)
                .Select(u => new { u.Username, u.Role })
                .ToListAsync(ct);

            foreach (var item in privilegedWithout2Fa)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "high",
                    Category: "Configurare",
                    Title:    $"@{item.Username} ({item.Role}) nu are 2FA activat",
                    Detail:   "Un cont privilegiat fără al doilea factor reduce securitatea " +
                               "întregului sistem la puterea unei singure parole.",
                    Username: item.Username));
            }

            // ── 5. Conturi blocate chiar acum ────────────────────────────────
            var lockedOut = await People()
                .Where(u => u.LockoutEndsAt != null && u.LockoutEndsAt > now)
                .OrderByDescending(u => u.LockoutEndsAt)
                .Take(MaxAlertsPerCategory)
                .Select(u => new { u.Username, u.LockoutEndsAt })
                .ToListAsync(ct);

            foreach (var item in lockedOut)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "low",
                    Category: "Autentificare",
                    Title:    $"Contul @{item.Username} este blocat",
                    Detail:   $"Deblocare automată la {item.LockoutEndsAt:dd.MM.yyyy HH:mm} UTC. " +
                               "Poate fi deblocat manual din pagina de utilizatori.",
                    Username: item.Username));
            }

            // ── 6. Utilizatori activi fara chei criptografice ────────────────
            // Nu pot primi fisiere: expeditorul nu are cu ce sa impacheteze cheia.
            // E o problema de functionare, nu de securitate, dar se manifesta ca
            // "nu pot trimite lui X" si e greu de diagnosticat din interfata.
            var withoutKeys = await People()
                .CountAsync(u => u.IsActive && u.PublicKeyEncryption == null, ct);

            if (withoutKeys > 0)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "low",
                    Category: "Configurare",
                    Title:    $"{withoutKeys} utilizatori activi nu și-au generat cheile",
                    Detail:   "Nu pot primi fișiere. Cheile se generează automat la prima " +
                               "autentificare, deci probabil nu s-au conectat încă.",
                    Username: (string?)null));
            }

            // ── 7. Refresh token refolosit ───────────────────────────────────
            // Un token deja rotit, prezentat din nou: doua parti au avut aceeasi
            // sesiune. Sesiunea s-a inchis automat, dar titularul trebuie
            // intrebat de unde s-a putut copia tokenul (calculator partajat,
            // extensie de browser, backup al profilului).
            var reusedTokens = await Logs()
                .Where(a => a.Action == AuditAction.RefreshTokenReused
                         && a.Timestamp >= last7d)
                .OrderByDescending(a => a.Timestamp)
                .Take(MaxAlertsPerCategory)
                .Select(a => new { a.Username, a.Timestamp, a.IpAddress })
                .ToListAsync(ct);

            foreach (var item in reusedTokens)
            {
                alerts.Add(new SecurityAlert(
                    Severity: "high",
                    Category: "Sesiuni",
                    Title:    $"Sesiune a lui @{item.Username} folosită de două părți",
                    Detail:   $"{item.Timestamp:dd.MM.yyyy HH:mm} UTC, cerere de la {item.IpAddress}. " +
                               "Sesiunea a fost închisă automat. Verificați cu utilizatorul de unde s-a " +
                               "putut copia tokenul și luați în calcul schimbarea parolei.",
                    Username: item.Username));
            }

            // ── 8. Conflicte de concurenta in rafala ─────────────────────────
            // Unul izolat e un dublu-clic. Multe de la aceeasi adresa in 24 de ore
            // inseamna cereri paralele trimise intentionat, de exemplu coduri 2FA
            // in rafala ca sa ocoleasca limita de incercari. Gruparea e pe IP, nu
            // pe cont: la login si 2FA cererea nu are inca utilizator. Tocmai de
            // aceea semnalul nu poate fi atribuit unei subdiviziuni si ramane
            // doar la administrator.
            if (visible is null)
            {
                var conflictBursts = await _context.AuditLogs
                    .Where(a => a.Action == AuditAction.ConcurrencyConflict
                             && a.Timestamp >= last24h)
                    .GroupBy(a => a.IpAddress)
                    .Select(g => new { Ip = g.Key, Count = g.Count() })
                    .Where(x => x.Count >= ConflictBurstThreshold)
                    .OrderByDescending(x => x.Count)
                    .Take(MaxAlertsPerCategory)
                    .ToListAsync(ct);

                foreach (var item in conflictBursts)
                {
                    alerts.Add(new SecurityAlert(
                        Severity: "medium",
                        Category: "Concurență",
                        Title:    $"{item.Count} cereri simultane respinse de la {item.Ip}",
                        Detail:   "În ultimele 24 de ore. Filtrați jurnalul după această adresă: cereri " +
                                   "paralele repetate pe autentificare sau 2FA indică o încercare automată.",
                        Username: (string?)null));
                }
            }

            // Severitatea decide ordinea. Un singur "high" ingropat sub zece "low"
            // e la fel de invizibil ca in jurnalul brut.
            var sorted = alerts.OrderBy(a => a.SeverityRank).ToList();

            return Ok(new
            {
                generatedAt = now,
                total       = sorted.Count,
                highCount   = sorted.Count(a => a.Severity == "high"),
                alerts      = sorted,
            });
        }

        /// <summary>
        /// O alertă afișată pe panoul de administrare.
        ///
        /// Tip propriu, nu obiect anonim: sortarea după severitate pe o listă de
        /// anonime ar cere reflecție, iar reflecția într-o buclă peste rezultate
        /// de bază de date e o soluție care funcționează exact până la prima
        /// redenumire de câmp, când eșuează la runtime în loc de compilare.
        /// </summary>
        private sealed record SecurityAlert(
            string Severity,
            string Category,
            string Title,
            string Detail,
            string? Username)
        {
            /// <summary>Ordinea de afișare. Nu se serializează spre client.</summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public int SeverityRank => Severity switch
            {
                "high"   => 0,
                "medium" => 1,
                _        => 2,
            };
        }

        /// <summary>De la câte eșecuri în 24h un cont devine semnal, nu zgomot.</summary>
        private const int FailedLoginAlertThreshold = 5;

        /// <summary>
        /// De la câte conflicte de concurență de pe același IP, în 24h, apare
        /// alerta. Un utilizator obișnuit ajunge la unul-două pe zi (dublu-clic);
        /// cinci înseamnă deja cereri trimise în paralel.
        /// </summary>
        private const int ConflictBurstThreshold = 5;

        /// <summary>
        /// Plafon per categorie. O pagină cu trei sute de alerte e o pagină pe
        /// care nimeni nu o citește; dacă limita e atinsă des, pragul e greșit.
        /// </summary>
        private const int MaxAlertsPerCategory = 10;

        // ═════════════════════════════════════════════════════════════════════
        // GET api/Stats/admin - panoul de administrare
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Toate cifrele de pe /admin, intr-un singur apel. Un singur round-trip
        /// in loc de sase: pagina fie se incarca intreaga, fie afiseaza o eroare -
        /// nu ajunge in starea hibrida in care doua carduri au date si patru arata
        /// zero fara explicatie.
        /// </summary>
        [HttpGet("admin")]
        [Authorize(Roles = nameof(UserRole.Administrator))]
        public async Task<IActionResult> GetAdminStats(
            [FromQuery] int days = 14,
            CancellationToken ct = default)
        {
            if (days is < 1 or > 90) days = 14;

            var now      = DateTime.UtcNow;
            var since    = now.Date.AddDays(-(days - 1));
            var last24h  = now.AddHours(-24);
            var next24h  = now.AddHours(24);

            // Sonda depozitului pornește prima și rulează în paralel cu interogările.
            // Nu folosește DbContext, deci nu concurează cu ele. Rezultatul se
            // așteaptă abia la final, iar sonda are oricum o limită de 3 secunde.
            var storageProbe = ProbeStorageAsync(ct);

            // ── Utilizatori ──────────────────────────────────────────────────
            var totalUsers   = await _context.Users.CountAsync(ct);
            var activeUsers  = await _context.Users.CountAsync(u => u.IsActive, ct);
            var lockedUsers  = await _context.Users
                .CountAsync(u => u.LockoutEndsAt != null && u.LockoutEndsAt > now, ct);

            // Utilizatorii fara chei nu pot primi fisiere criptate. E cifra pe care
            // un administrator trebuie sa o urmareasca: fiecare cont din lista asta
            // e un destinatar catre care uploadul va fi refuzat cu 400.
            var usersWithoutKeys = await _context.Users
                .CountAsync(u => u.IsActive && u.PublicKeyEncryption == null, ct);

            var usersByRole = await _context.Users
                .GroupBy(u => u.Role)
                .Select(g => new { role = g.Key, count = g.Count() })
                .ToListAsync(ct);

            // ── Transferuri ──────────────────────────────────────────────────
            var totalTransfers      = await _context.FileTransfers.CountAsync(ct);
            var pendingTransfers    = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Pending, ct);
            var downloadedTransfers = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Downloaded, ct);
            var expiredTransfers    = await _context.FileTransfers.CountAsync(t => t.Status == TransferStatus.Expired, ct);
            var encryptedTransfers  = await _context.FileTransfers.CountAsync(t => t.IsEncrypted, ct);

            var deletedTransfers    = await _context.FileTransfers.CountAsync(t => t.DeletedAt != null, ct);

            // Coada jobului de expirare: exact selectia jobului (vezi
            // TransferExpirationService). Daca numarul creste de la o reincarcare
            // la alta, jobul nu tine pasul sau a murit.
            var awaitingPurge = await _context.FileTransfers
                .CountAsync(t => t.ExpiresAt != null
                              && t.ExpiresAt < now
                              && t.DeletedAt == null
                              && (t.Status == TransferStatus.Pending
                                  || (t.Status == TransferStatus.Downloaded && t.StorageKey != "")), ct);

            var expiringNext24h = await _context.FileTransfers
                .CountAsync(t => t.ExpiresAt != null
                              && t.ExpiresAt >= now
                              && t.ExpiresAt < next24h
                              && t.DeletedAt == null
                              && t.Status == TransferStatus.Pending, ct);

            // Contabilitatea se face pe cifrotext, nu pe dimensiunea in clar:
            // octetii care ocupa efectiv spatiu in bucket sunt cei criptati.
            // Intra doar randurile care mai au obiect in depozit (StorageKey
            // nevid): expirate, retrase si sterse il au golit.
            var storedCiphertextBytes = await _context.FileTransfers
                .Where(t => t.StorageKey != "" && t.Status != TransferStatus.Expired)
                .SumAsync(t => (long?)t.CiphertextSize, ct) ?? 0;

            var totalCiphertextBytes = await _context.FileTransfers
                .SumAsync(t => (long?)t.CiphertextSize, ct) ?? 0;

            // ── Documente ────────────────────────────────────────────────────
            var totalDocuments       = await _context.Documents.CountAsync(ct);
            var totalDocumentVersions = await _context.DocumentVersions.CountAsync(ct);

            // ── Securitate ───────────────────────────────────────────────────
            var failedLoginsLast24h = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.Login &&
                a.Result == AuditResult.Failure &&
                a.Timestamp >= last24h, ct);

            var successfulLoginsLast24h = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.Login &&
                a.Result == AuditResult.Success &&
                a.Timestamp >= last24h, ct);

            // Semnaturi invalide raportate de clienti. Zero e valoarea asteptata;
            // orice altceva inseamna fie o cheie schimbata fara reimpachetare, fie
            // ceva ce merita investigat manual.
            var invalidSignatures = await _context.AuditLogs.CountAsync(a =>
                a.Action == AuditAction.FileDownload &&
                a.Result == AuditResult.Warning, ct);

            // ── Serii temporale ──────────────────────────────────────────────
            // Gruparea se face in baza de date (date_trunc), nu in memorie: pe un
            // jurnal de audit de sute de mii de randuri, aducerea tuturor in proces
            // ca sa numeri pe zile e exact genul de query care omoara serverul.
            var auditByDay = await _context.AuditLogs
                .Where(a => a.Timestamp >= since)
                .GroupBy(a => new { Day = a.Timestamp.Date, a.Action })
                .Select(g => new { g.Key.Day, g.Key.Action, Count = g.Count() })
                .ToListAsync(ct);

            var transfersByDay = await _context.FileTransfers
                .Where(t => t.CreatedAt >= since)
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { Day = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.CiphertextSize) })
                .ToListAsync(ct);

            var failedLoginsByDay = await _context.AuditLogs
                .Where(a => a.Timestamp >= since
                         && a.Action == AuditAction.Login
                         && a.Result == AuditResult.Failure)
                .GroupBy(a => a.Timestamp.Date)
                .Select(g => new { Day = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            // Zilele fara activitate lipsesc din rezultatul SQL. Daca le-am trimite
            // asa, graficul ar comprima axa si ar arata o crestere care nu exista.
            // Completam seria pe client-side-ul serverului, nu in React.
            var series = new List<object>(days);
            for (var i = 0; i < days; i++)
            {
                var day = since.AddDays(i);

                var uploads = auditByDay
                    .Where(x => x.Day == day && x.Action == AuditAction.FileUpload)
                    .Sum(x => x.Count);

                var downloads = auditByDay
                    .Where(x => x.Day == day && x.Action == AuditAction.FileDownload)
                    .Sum(x => x.Count);

                var expirations = auditByDay
                    .Where(x => x.Day == day && x.Action == AuditAction.TransferExpired)
                    .Sum(x => x.Count);

                var created = transfersByDay.FirstOrDefault(x => x.Day == day);
                var failed  = failedLoginsByDay.FirstOrDefault(x => x.Day == day);

                series.Add(new
                {
                    date         = day.ToString("yyyy-MM-dd"),
                    label        = day.ToString("dd.MM"),
                    uploads,
                    downloads,
                    expirations,
                    transfers    = created?.Count ?? 0,
                    bytes        = created?.Bytes ?? 0,
                    failedLogins = failed?.Count ?? 0,
                });
            }

            // ── Top expeditori ───────────────────────────────────────────────
            var topSenders = await _context.FileTransfers
                .Where(t => t.CreatedAt >= since)
                .Include(t => t.Sender)
                .GroupBy(t => new { t.SenderId, Name = t.Sender!.FullName ?? t.Sender.Username })
                .Select(g => new
                {
                    userId = g.Key.SenderId,
                    name   = g.Key.Name,
                    count  = g.Count(),
                    bytes  = g.Sum(x => x.CiphertextSize),
                })
                .OrderByDescending(x => x.count)
                .Take(5)
                .ToListAsync(ct);

            // ── Starea modulelor ─────────────────────────────────────────────
            var (dbOnline, dbLatencyMs)           = await ProbeDatabaseAsync(ct);
            var (storageOnline, storageLatencyMs) = await storageProbe;

            return Ok(new
            {
                generatedAt = now,
                windowDays  = days,

                users = new
                {
                    total          = totalUsers,
                    active         = activeUsers,
                    inactive       = totalUsers - activeUsers,
                    locked         = lockedUsers,
                    withoutKeys    = usersWithoutKeys,
                    byRole = usersByRole.Select(x => new
                    {
                        role  = x.role.ToString(),
                        label = RoleLabel(x.role),
                        count = x.count,
                    }),
                },

                transfers = new
                {
                    total           = totalTransfers,
                    pending         = pendingTransfers,
                    downloaded      = downloadedTransfers,
                    expired         = expiredTransfers,
                    deleted         = deletedTransfers,
                    encrypted       = encryptedTransfers,
                    // Procentul de transferuri care trec efectiv prin plicul E2E.
                    // Inlocuieste "Integritate sistem: 100%", care era o constanta.
                    encryptedPercent = totalTransfers == 0
                        ? 100
                        : (int)Math.Round(encryptedTransfers * 100.0 / totalTransfers),
                    awaitingPurge   = awaitingPurge,
                    expiringNext24h = expiringNext24h,
                },

                storage = new
                {
                    provider              = _storage.ProviderName,
                    bucket                = _storageOptions.Bucket,
                    presignedDownload     = _storageOptions.UsePresignedDownload && _storage.SupportsPresignedUrls,
                    maxFileSizeMb         = _storageOptions.MaxFileSizeMb,
                    storedCiphertextBytes,
                    totalCiphertextBytes,
                    // Criptarea la nivel de aplicație a documentelor (nu a
                    // transferurilor, care sunt E2EE). Doar identificatorii
                    // cheilor, niciodată valorile.
                    encryption = _storage is EncryptingFileStorage enc
                        ? new
                        {
                            enabled               = enc.WritesEncrypted,
                            activeKeyId           = enc.KeyRing?.ActiveKeyId,
                            keyCount              = enc.KeyRing?.KeyIds.Count ?? 0,
                            prefixes              = enc.Options.PrefixList,
                            allowPlaintextRead    = enc.Options.AllowPlaintextRead,
                            verifyBeforeStreaming = enc.Options.VerifyBeforeStreaming,
                        }
                        : null,
                },

                documents = new
                {
                    total    = totalDocuments,
                    versions = totalDocumentVersions,
                },

                security = new
                {
                    failedLoginsLast24h,
                    successfulLoginsLast24h,
                    invalidSignatures,
                },

                series,
                topSenders,

                expirationJob = new
                {
                    enabled         = _expirationOptions.Enabled,
                    intervalMinutes = _expirationOptions.IntervalMinutes,
                    purgeObjects    = _expirationOptions.PurgeObjects,
                    status          = _expirationState.Status,
                    lastRunAt       = _expirationState.LastRunAt,
                    lastSuccessAt   = _expirationState.LastSuccessAt,
                    lastError       = _expirationState.LastError,
                    runCount        = _expirationState.RunCount,
                    lastRunExpired  = _expirationState.LastRunExpired,
                    lastRunPurged   = _expirationState.LastRunPurged,
                    expiredTotal    = _expirationState.ExpiredTotal,
                    purgedTotal     = _expirationState.PurgedTotal,
                    failedPurgeTotal = _expirationState.FailedPurgeTotal,
                },

                modules = new[]
                {
                    new { name = "API (.NET 8)",                    online = true,          latencyMs = 0 },
                    new { name = "Baza de date (PostgreSQL)",       online = dbOnline,      latencyMs = dbLatencyMs },
                    new { name = $"Depozit fisiere ({_storage.ProviderName})", online = storageOnline, latencyMs = storageLatencyMs },
                    new { name = "Job expirare transferuri",        online = _expirationOptions.Enabled && _expirationState.Status != "Eroare", latencyMs = 0 },
                },
            });
        }
        
        [HttpPost("expiration/run")]
        [Authorize(Roles = nameof(UserRole.Administrator))]
        public async Task<IActionResult> RunExpiration(
            [FromServices] TransferExpirationService job,
            CancellationToken ct)
        {
            if (!_expirationOptions.Enabled)
                return StatusCode(503, new { message = "Jobul de expirare este dezactivat din configurare." });

            try
            {
                var (expired, purged, failed) = await job.RunOnceAsync(ct);

                return Ok(new
                {
                    message = failed == 0
                        ? $"{expired} transferuri expirate, {purged} obiecte sterse din depozit."
                        : $"{expired} transferuri expirate, {purged} obiecte sterse, {failed} esecuri la stergere.",
                    expired,
                    purged,
                    failed,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rularea manuala a jobului de expirare a esuat.");
                return StatusCode(500, new { message = "Rularea jobului a esuat. Verificati jurnalul serverului." });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string RoleLabel(UserRole role) => role switch
        {
            UserRole.Utilizator    => "Utilizator",
            UserRole.SefDirectie   => "Sef de Directie",
            UserRole.Administrator => "Administrator",
            _                      => role.ToString(),
        };

        /// <summary>
        /// Latenta reala a bazei de date, masurata pe o interogare triviala.
        /// Inlocuieste "24ms" scris in JSX.
        /// </summary>
        /// <summary>
        /// Durata maximă a unei sonde; aceeași valoare ca în HealthController.
        ///
        /// Fără limită, o sondă către un depozit oprit sau inaccesibil aștepta cât
        /// îi permiteau reîncercările clientului S3 - pe Windows, zeci de secunde.
        /// Tot răspunsul /api/Stats/admin aștepta după ea, iar browserul renunța
        /// după 30 de secunde cu „Serverul nu răspunde”, deși API-ul funcționa.
        /// Acum pagina se încarcă, iar modulul afectat apare ca indisponibil.
        /// </summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        private async Task<(bool Online, int LatencyMs)> ProbeDatabaseAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var sw = Stopwatch.StartNew();
            try
            {
                var canConnect = await _context.Database.CanConnectAsync(cts.Token);
                sw.Stop();
                return (canConnect, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                if (!ct.IsCancellationRequested)
                    _logger.LogWarning(ex, "Sonda catre baza de date a esuat sau a depasit {Seconds} s.",
                        ProbeTimeout.TotalSeconds);
                return (false, (int)sw.ElapsedMilliseconds);
            }
        }
        
        /// <summary>
        /// Nu aruncă niciodată: rulează în paralel cu interogările, iar o excepție
        /// neobservată nu trebuie să transforme o sondă eșuată într-un răspuns 500.
        /// </summary>
        private async Task<(bool Online, int LatencyMs)> ProbeStorageAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var sw = Stopwatch.StartNew();
            try
            {
                await _storage.ExistsAsync(
                    $"{_storageOptions.TransfersPrefix}/.healthcheck-{Guid.NewGuid():N}", cts.Token);
                sw.Stop();
                return (true, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                if (!ct.IsCancellationRequested)
                    _logger.LogWarning(ex, "Sonda catre depozitul de fisiere a esuat sau a depasit {Seconds} s.",
                        ProbeTimeout.TotalSeconds);
                return (false, (int)sw.ElapsedMilliseconds);
            }
        }
    }
}