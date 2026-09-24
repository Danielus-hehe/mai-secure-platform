using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using MAI.BusinessLogic.Dtos;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Jurnalul de audit: cine a trimis ce, cui, de pe ce IP, cu ce rezultat.
    ///
    /// Rolul se cere pe CLASĂ, spre deosebire de UsersController: aici nu există
    /// niciun endpoint pe care un utilizator obișnuit să aibă motiv să îl apeleze.
    /// Până în 2026-09-10 clasa avea doar [Authorize], iar lista și dropdown-ul
    /// de utilizatori erau citibile de orice cont autentificat - pagina /audit
    /// era ascunsă doar în meniul frontend-ului, nu și în API.
    /// </summary>
    [Authorize(Roles = "Administrator,SefDirectie")]
    [ApiController]
    [Route("api/[controller]")]
    public class AuditLogsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AuditLogsController> _logger;

        /// <summary>Plafon la export. Peste atât, utilizatorul trebuie să restrângă intervalul.</summary>
        private const int MaxExportRows = 50_000;

        public AuditLogsController(AppDbContext context, ILogger<AuditLogsController> logger)
        {
            _context = context;
            _logger  = logger;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET api/AuditLogs?username=&action=&result=&search=&from=&to=&page=&pageSize=
        // ─────────────────────────────────────────────────────────────────────
        [Authorize(Roles = "Administrator,SefDirectie")]
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? username,
            [FromQuery] string? action,
            [FromQuery] string? result,
            [FromQuery] string? search,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken ct = default)
        {
            var pagination = new PaginationQuery { Page = page, PageSize = pageSize };
            var query      = BuildQuery(username, action, result, search, from, to);

            // Count înainte de Skip/Take - altfel numărăm doar pagina curentă.
            var total = await query.CountAsync(ct);

            var raw = await query
                .OrderByDescending(a => a.Timestamp)
                .Skip(pagination.Skip)
                .Take(pagination.PageSize)
                .ToListAsync(ct);

            var items = raw.Select(Project).ToList();

            return Ok(PagedResult<AuditEntryDto>.Create(items, total, pagination));
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET api/AuditLogs/export?format=xlsx|csv + aceleași filtre
        // ─────────────────────────────────────────────────────────────────────
        [Authorize(Roles = "Administrator,SefDirectie")]
        [HttpGet("export")]
        public async Task<IActionResult> Export(
            [FromQuery] string format = "xlsx",
            [FromQuery] string? username = null,
            [FromQuery] string? action = null,
            [FromQuery] string? result = null,
            [FromQuery] string? search = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            CancellationToken ct = default)
        {
            var query = BuildQuery(username, action, result, search, from, to);
            var total = await query.CountAsync(ct);

            if (total > MaxExportRows)
            {
                return BadRequest(new
                {
                    message = $"Exportul depășește limita de {MaxExportRows:N0} înregistrări " +
                              $"({total:N0} găsite). Restrângeți intervalul de date sau filtrele.",
                    totalCount = total,
                });
            }

            var raw   = await query.OrderByDescending(a => a.Timestamp).ToListAsync(ct);
            var items = raw.Select(Project).ToList();

            // Exportul jurnalului de audit este el însuși o acțiune auditabilă.
            _context.AuditLogs.Add(new AuditLog
            {
                Username  = HttpContext.User.Identity?.Name ?? "sistem",
                Action    = AuditAction.FileDownload,
                Details   = $"Export jurnal audit ({items.Count} inregistrari, {format})",
                Result    = AuditResult.Success,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Timestamp = DateTime.UtcNow,
            });
            await _context.SaveChangesAsync(ct);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);

            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(items);
                return File(csv, "text/csv; charset=utf-8", $"jurnal_audit_{stamp}.csv");
            }

            var xlsx = BuildXlsx(items, username, action, result, search, from, to);
            return File(
                xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"jurnal_audit_{stamp}.xlsx");
        }

        // GET api/AuditLogs/usernames - pentru dropdown-ul de filtrare
        [Authorize(Roles = "Administrator,SefDirectie")]
        [HttpGet("usernames")]
        public async Task<IActionResult> GetUsernames(CancellationToken ct)
        {
            var names = await _context.AuditLogs
                .Select(a => a.Username)
                .Distinct()
                .OrderBy(n => n)
                .ToListAsync(ct);
            return Ok(names);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construirea query-ului - o singură sursă de adevăr pentru listă și export
        // ─────────────────────────────────────────────────────────────────────
        private IQueryable<AuditLog> BuildQuery(
            string? username, string? action, string? result,
            string? search, DateTime? from, DateTime? to)
        {
            var query = _context.AuditLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(username))
                query = query.Where(a => a.Username == username);

            if (!string.IsNullOrWhiteSpace(action))
            {
                var enums = MapFrontendAction(action);
                if (enums is { Length: > 0 })
                    query = query.Where(a => enums.Contains(a.Action));
            }

            // Filtrarea se face pe coloana Result, indexata, nu cu StartsWith pe
            // un camp de text liber. Randurile scrise inainte de migrare au fost
            // convertite din prefixe de catre scriptul 004; nimic din codul nou
            // nu mai scrie prefixe.
            var resultFilter = ParseResult(result);
            if (resultFilter.HasValue)
                query = query.Where(a => a.Result == resultFilter.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                // ILike = LIKE case-insensitive în PostgreSQL. Traducerea se face de Npgsql,
                // deci filtrarea rămâne în baza de date, nu în memorie.
                query = query.Where(a =>
                    EF.Functions.ILike(a.Username, $"%{term}%") ||
                    EF.Functions.ILike(a.Details, $"%{term}%") ||
                    EF.Functions.ILike(a.IpAddress, $"%{term}%"));
            }

            if (from.HasValue)
            {
                var fromUtc = DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc);
                query = query.Where(a => a.Timestamp >= fromUtc);
            }

            if (to.HasValue)
            {
                // Inclusiv ziua "to" în întregime.
                var toUtc = DateTime.SpecifyKind(to.Value.Date.AddDays(1), DateTimeKind.Utc);
                query = query.Where(a => a.Timestamp < toUtc);
            }

            return query;
        }

        private static AuditEntryDto Project(AuditLog a) => new()
        {
            Id        = a.Id,
            Timestamp = a.Timestamp,
            UserId    = a.UserId,
            UserName  = a.Username,
            Action    = MapBackendAction(a.Action),
            Target    = StripLegacyPrefix(a.Details),
            IpAddress = a.IpAddress,
            Result    = ResultLabel(a.Result),
        };

        /// <summary>
        /// Numele de rezultat asteptat de frontend. Ramane text in DTO ca sa nu
        /// legam interfata de valorile numerice ale enum-ului.
        /// </summary>
        private static string ResultLabel(AuditResult r) => r switch
        {
            AuditResult.Failure => "ESEC",
            AuditResult.Warning => "ATENTIE",
            _                   => "SUCCES",
        };

        private static AuditResult? ParseResult(string? s) => s?.ToUpperInvariant() switch
        {
            "SUCCES"  => AuditResult.Success,
            "ESEC"    => AuditResult.Failure,
            "ATENTIE" => AuditResult.Warning,
            _         => null,
        };

        /// <summary>
        /// Curata prefixul de rezultat din randurile istorice. Scriptul de migrare
        /// il elimina din baza de date, dar un jurnal restaurat dintr-un backup mai
        /// vechi l-ar readuce; e mai ieftin sa tolerezi aici decat sa afisezi
        /// "ESEC: ESEC" in raport.
        /// </summary>
        private static string StripLegacyPrefix(string details)
        {
            foreach (var prefix in LegacyPrefixes)
                if (details.StartsWith(prefix, StringComparison.Ordinal))
                    return details[prefix.Length..].Trim();

            return details;
        }

        private static readonly string[] LegacyPrefixes = ["SUCCES:", "ESEC:", "ATENTIE:"];

        // ─────────────────────────────────────────────────────────────────────
        // Generare fișiere
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] BuildCsv(IReadOnlyList<AuditEntryDto> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp;Utilizator;Actiune;Detalii;Adresa IP;Rezultat");

            foreach (var e in items)
            {
                sb.Append(Csv(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(';');
                sb.Append(Csv(e.UserName)).Append(';');
                sb.Append(Csv(ActionLabel(e.Action))).Append(';');
                sb.Append(Csv(e.Target)).Append(';');
                sb.Append(Csv(e.IpAddress)).Append(';');
                sb.AppendLine(Csv(e.Result));
            }

            // BOM UTF-8: fără el, Excel pe Windows deschide fișierul în ANSI
            // și diacriticele românești apar ca gunoi.
            var preamble = Encoding.UTF8.GetPreamble();
            var body     = Encoding.UTF8.GetBytes(sb.ToString());
            var output   = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
            return output;
        }

        /// <summary>
        /// Escapare CSV. Fără ea, un câmp Details care conține ; sau ghilimele
        /// sparge coloanele. Prefixul cu apostrof apără de CSV injection: o valoare
        /// care începe cu = sau + ar fi interpretată de Excel ca formulă.
        /// </summary>
        private static string Csv(string? value)
        {
            var v = value ?? string.Empty;

            if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@'))
                v = "'" + v;

            if (v.Contains('"') || v.Contains(';') || v.Contains('\n') || v.Contains('\r'))
                return '"' + v.Replace("\"", "\"\"") + '"';

            return v;
        }

        private static byte[] BuildXlsx(
            IReadOnlyList<AuditEntryDto> items,
            string? username, string? action, string? result,
            string? search, DateTime? from, DateTime? to)
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("Jurnal audit");

            // Antet cu context: un raport exportat trebuie să spună singur ce conține.
            sheet.Cell(1, 1).Value = "MAI - Jurnal de audit SGDM";
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(1, 1).Style.Font.FontSize = 14;
            sheet.Range(1, 1, 1, 6).Merge();

            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(username)) filters.Add($"utilizator={username}");
            if (!string.IsNullOrWhiteSpace(action))   filters.Add($"actiune={action}");
            if (!string.IsNullOrWhiteSpace(result))   filters.Add($"rezultat={result}");
            if (!string.IsNullOrWhiteSpace(search))   filters.Add($"cautare='{search}'");
            if (from.HasValue) filters.Add($"de la {from:yyyy-MM-dd}");
            if (to.HasValue)   filters.Add($"pana la {to:yyyy-MM-dd}");

            sheet.Cell(2, 1).Value =
                $"Generat: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {items.Count} inregistrari" +
                (filters.Count > 0 ? $" · Filtre: {string.Join(", ", filters)}" : " · Fara filtre");
            sheet.Cell(2, 1).Style.Font.FontSize = 9;
            sheet.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;
            sheet.Range(2, 1, 2, 6).Merge();

            const int headerRow = 4;
            string[] headers = ["Timestamp", "Utilizator", "Acțiune", "Detalii", "Adresă IP", "Rezultat"];
            for (var i = 0; i < headers.Length; i++)
            {
                var cell = sheet.Cell(headerRow, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A5F");
                cell.Style.Font.FontColor = XLColor.White;
            }

            var row = headerRow + 1;
            foreach (var e in items)
            {
                sheet.Cell(row, 1).Value = e.Timestamp;
                sheet.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                sheet.Cell(row, 2).SetValue(e.UserName ?? string.Empty);
                sheet.Cell(row, 3).SetValue(ActionLabel(e.Action) ?? string.Empty);
                sheet.Cell(row, 4).SetValue(e.Target ?? string.Empty);
                sheet.Cell(row, 5).SetValue(e.IpAddress ?? string.Empty);
                sheet.Cell(row, 6).SetValue(e.Result ?? string.Empty);
                sheet.Cell(row, 4).Style.NumberFormat.Format = "@";

                if (e.Result == "ESEC")
                {
                    sheet.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEE2E2");
                    sheet.Cell(row, 6).Style.Font.Bold = true;
                }
                else if (e.Result == "ATENTIE")
                {
                    sheet.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7");
                    sheet.Cell(row, 6).Style.Font.Bold = true;
                }
                row++;
            }

            if (items.Count > 0)
            {
                sheet.Range(headerRow, 1, row - 1, 6).SetAutoFilter();
                sheet.SheetView.FreezeRows(headerRow);
            }

            sheet.Columns(1, 6).AdjustToContents();
            sheet.Column(4).Width = Math.Min(sheet.Column(4).Width, 60);

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mapări enum
        // ─────────────────────────────────────────────────────────────────────

        private static string ActionLabel(string action) => action switch
        {
            "LOGIN"          => "Autentificare",
            "LOGOUT"         => "Deconectare",
            "UPLOAD"         => "Încărcare",
            "DOWNLOAD"       => "Descărcare",
            "MODIFICARE_DOC" => "Modificare document",
            "TRANSFER"       => "Transfer (retragere, expirare, redirecționare)",
            "SECURITATE"     => "Securitate cont (parolă, sesiuni)",
            "STRUCTURA"      => "Structură organizatorică",
            "DOC_INTERN"     => "Document intern",
            "ADMIN"          => "Administrare",
            _                => action,
        };

        private static string MapBackendAction(AuditAction a) => a switch
        {
            AuditAction.Login              => "LOGIN",
            AuditAction.Logout             => "LOGOUT",
            AuditAction.FileUpload         => "UPLOAD",
            AuditAction.FileDownload       => "DOWNLOAD",
            AuditAction.DocumentCreate     => "MODIFICARE_DOC",
            AuditAction.DocumentNewVersion => "MODIFICARE_DOC",
            AuditAction.UserCreated        => "ADMIN",
            AuditAction.UserUpdated        => "ADMIN",
            AuditAction.FileDeleted or AuditAction.TransferExpired or
            AuditAction.TransferRevoked or AuditAction.TransferForwarded      => "TRANSFER",
            AuditAction.SessionRevoked or AuditAction.PasswordResetRequested or
            AuditAction.PasswordResetCompleted or AuditAction.StorageIntegrityFailure or
            AuditAction.StorageRecrypted or AuditAction.RefreshTokenReused or
            AuditAction.ConcurrencyConflict                                   => "SECURITATE",
            AuditAction.OrgStructureChanged                                   => "STRUCTURA",
            AuditAction.InternalDocumentCreated or AuditAction.InternalDocumentPublished or
            AuditAction.InternalDocumentOpened or AuditAction.InternalDocumentAcknowledged or
            AuditAction.InternalDocumentRepealed                              => "DOC_INTERN",
            _                              => "ADMIN",
        };

        /// <summary>
        /// Filtrul din interfață (o categorie) → acțiunile din baza de date.
        ///
        /// Derivat din <see cref="MapBackendAction"/>, nu scris a doua oară de
        /// mână. Înainte erau două liste paralele și divergeau: acțiunile de
        /// domeniu (DirectoryAccountProvisioned etc.) apăreau în jurnal ca
        /// „ADMIN”, dar filtrul „ADMIN” nu le găsea. Acum orice acțiune nouă
        /// intră automat în filtrul categoriei în care e afișată.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, AuditAction[]> ActionsByCategory =
            Enum.GetValues<AuditAction>()
                .GroupBy(MapBackendAction)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        private static AuditAction[]? MapFrontendAction(string s) =>
            ActionsByCategory.TryGetValue(s, out var actions) ? actions : null;
    }

    /// <summary>Forma trimisă spre frontend pentru o înregistrare de audit.</summary>
    public class AuditEntryDto
    {
        public Guid Id { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid? UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }
}