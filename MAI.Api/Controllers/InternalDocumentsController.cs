using MAI.Api.Security;
using MAI.Api.Services;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Organization;
using MAI.BusinessLogic.Storage;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;

namespace MAI.Api.Controllers
{
    /// <summary>
    /// Documente interne distribuite pe structura organizatorică, cu confirmare
    /// „Luat la cunoștință” și raport pentru autor.
    ///
    /// Ciclul de viață: Ciornă → Publicat → (Abrogat). Distribuția se rezolvă
    /// și se verifică pe server (DistributionResolver) la creare, ca autorul să
    /// afle imediat dacă alegerea e permisă, și din nou la publicare, când lista
    /// destinatarilor se fixează definitiv.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class InternalDocumentsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IFileStorage _storage;
        private readonly StorageOptions _storageOptions;
        private readonly ILogger<InternalDocumentsController> _logger;

        private const string StoragePrefix = "internal";
        private const int MaxTargets = 500;

        /// <summary>
        /// Tipurile servite cu Content-Type propriu. Orice altceva pleacă drept
        /// application/octet-stream: un HTML încărcat ca „document” nu are voie
        /// să ajungă în browser cu tipul text/html.
        /// </summary>
        private static readonly HashSet<string> SafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.oasis.opendocument.text",
            "text/plain",
            "image/png",
            "image/jpeg",
        };

        public InternalDocumentsController(
            AppDbContext context,
            IFileStorage storage,
            StorageOptions storageOptions,
            ILogger<InternalDocumentsController> logger)
        {
            _context        = context;
            _storage        = storage;
            _storageOptions = storageOptions;
            _logger         = logger;
        }

        private Guid CurrentUserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        private string CurrentUsername => User.Identity?.Name ?? "sistem";
        private string CallerIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        private bool IsAdministrator =>
            User.IsInRole(nameof(UserRole.Administrator))
            || User.FindFirst(ClaimTypes.Role)?.Value == ((int)UserRole.Administrator).ToString();

        // ═════════════════════════════════════════════════════════════════════
        // GET api/InternalDocuments/distribution-options
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>Ce poate alege autorul curent în formularul de distribuție.</summary>
        [HttpGet("distribution-options")]
        public async Task<IActionResult> GetDistributionOptions(CancellationToken ct)
        {
            var tree = await OrgStructure.LoadTreeAsync(_context, ct);
            var caps = DistributionResolver.CapabilitiesFor(CurrentUserId, IsAdministrator, tree);

            return Ok(new
            {
                ledUnitId         = caps.LedUnitId,
                ledUnitName       = caps.LedUnitId is { } id ? tree.PathOf(id) : null,
                isAdmin           = caps.IsAdmin,
                modes             = caps.Modes,
                selectableUnitIds = caps.SelectableUnitIds,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/InternalDocuments/preview-distribution
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Câți destinatari ar avea distribuția aleasă și cum se împart pe
        /// subdiviziuni — afișat în formular înainte de publicare, ca autorul să
        /// vadă „32 de persoane în 4 subdiviziuni”, nu doar opțiunea bifată.
        /// </summary>
        [HttpPost("preview-distribution")]
        public async Task<IActionResult> PreviewDistribution([FromBody] DistributionInput? input, CancellationToken ct)
        {
            if (input is null) return BadRequest(new { message = "Distribuția lipsește." });

            var (result, tree, members) = await ResolveAsync(input, ct);
            if (!result.Success) return BadRequest(new { message = result.Error });

            var byUser = members.ToDictionary(m => m.Id);
            var units = result.RecipientIds
                .GroupBy(id => byUser.TryGetValue(id, out var m) ? m.OrgUnitId : null)
                .Select(g => new
                {
                    orgUnitId = g.Key,
                    name      = g.Key is { } uid ? tree.PathOf(uid) : "Neîncadrați",
                    count     = g.Count(),
                })
                .OrderByDescending(x => x.count)
                .ToList();

            return Ok(new { count = result.RecipientIds.Count, units });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/InternalDocuments
        // ═════════════════════════════════════════════════════════════════════
        /// <param name="box">„inbox” (primite, implicit) sau „authored” (create de mine).</param>
        /// <param name="pending">Doar cele care îmi cer încă „Luat la cunoștință”.</param>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? box,
            [FromQuery] string? search,
            [FromQuery] InternalDocumentStatus? status,
            [FromQuery] bool pending = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken ct = default)
        {
            var userId     = CurrentUserId;
            var authored   = string.Equals(box, "authored", StringComparison.OrdinalIgnoreCase);
            var pagination = new PaginationQuery { Page = page, PageSize = pageSize };

            var query = _context.InternalDocuments.AsNoTracking();

            query = authored
                ? query.Where(d => d.AuthorId == userId)
                : query.Where(d => d.Status != InternalDocumentStatus.Draft
                                && d.Recipients.Any(r => r.UserId == userId));

            if (status.HasValue && Enum.IsDefined(status.Value))
                query = query.Where(d => d.Status == status.Value);

            if (pending && !authored)
                query = query.Where(d => d.Status == InternalDocumentStatus.Published
                                      && d.RequiresAcknowledgement
                                      && d.Recipients.Any(r => r.UserId == userId && r.AcknowledgedAt == null));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                query = query.Where(d =>
                    EF.Functions.ILike(d.Title, term) ||
                    (d.Number != null && EF.Functions.ILike(d.Number, term)) ||
                    (d.Summary != null && EF.Functions.ILike(d.Summary, term)) ||
                    (d.Author != null && d.Author.FullName != null && EF.Functions.ILike(d.Author.FullName, term)));
            }

            var total = await query.CountAsync(ct);

            var items = await query
                .OrderByDescending(d => d.PublishedAt ?? d.CreatedAt).ThenBy(d => d.Id)
                .Skip(pagination.Skip)
                .Take(pagination.PageSize)
                .Select(d => new InternalDocumentListItem
                {
                    Id                      = d.Id,
                    Title                   = d.Title,
                    Number                  = d.Number,
                    Summary                 = d.Summary,
                    Status                  = d.Status,
                    DistributionMode        = d.DistributionMode,
                    RequiresAcknowledgement = d.RequiresAcknowledgement,
                    AuthorId                = d.AuthorId,
                    AuthorName              = d.Author != null ? (d.Author.FullName ?? d.Author.Username) : "—",
                    AuthorUnitName          = d.AuthorOrgUnit != null ? d.AuthorOrgUnit.Name : null,
                    FileName                = d.FileName,
                    FileSize                = d.FileSize,
                    CreatedAt               = d.CreatedAt,
                    PublishedAt             = d.PublishedAt,
                    RepealedAt              = d.RepealedAt,
                    IsAuthor                = d.AuthorId == userId,
                    MyOpenedAt              = d.Recipients.Where(r => r.UserId == userId).Select(r => r.FirstOpenedAt).FirstOrDefault(),
                    MyAcknowledgedAt        = d.Recipients.Where(r => r.UserId == userId).Select(r => r.AcknowledgedAt).FirstOrDefault(),
                    RecipientCount          = d.AuthorId == userId ? d.Recipients.Count() : null,
                    OpenedCount             = d.AuthorId == userId ? d.Recipients.Count(r => r.FirstOpenedAt != null) : null,
                    AcknowledgedCount       = d.AuthorId == userId ? d.Recipients.Count(r => r.AcknowledgedAt != null) : null,
                })
                .ToListAsync(ct);

            return Ok(PagedResult<InternalDocumentListItem>.Create(items, total, pagination));
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/InternalDocuments/pending-count
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>Documentele care îmi cer „Luat la cunoștință” — pentru insigna din meniu.</summary>
        [HttpGet("pending-count")]
        public async Task<IActionResult> GetPendingCount(CancellationToken ct)
        {
            var userId = CurrentUserId;
            var count = await _context.InternalDocumentRecipients.CountAsync(r =>
                r.UserId == userId &&
                r.AcknowledgedAt == null &&
                r.Document!.Status == InternalDocumentStatus.Published &&
                r.Document.RequiresAcknowledgement, ct);

            return Ok(new { count });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/InternalDocuments/{id}
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;

            var doc = await _context.InternalDocuments
                .AsNoTracking()
                .Include(d => d.Author)
                .Include(d => d.AuthorOrgUnit)
                .Include(d => d.Targets)
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });

            var mine = await _context.InternalDocumentRecipients
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.DocumentId == id && r.UserId == userId, ct);

            if (!CanRead(doc, mine)) return Forbid();

            var tree = await OrgStructure.LoadTreeAsync(_context, ct);
            var targetUserIds = doc.Targets.Where(t => t.Kind == DistributionTargetKind.User).Select(t => t.TargetId).ToList();
            var targetUsers = await _context.Users
                .AsNoTracking()
                .Where(u => targetUserIds.Contains(u.Id))
                .Select(u => new { id = u.Id, name = u.FullName ?? u.Username })
                .ToListAsync(ct);

            var isAuthor = doc.AuthorId == userId;
            object? counts = null;
            if (isAuthor || IsAdministrator)
            {
                var recipients = _context.InternalDocumentRecipients.Where(r => r.DocumentId == id);
                counts = new
                {
                    total        = await recipients.CountAsync(ct),
                    opened       = await recipients.CountAsync(r => r.FirstOpenedAt != null, ct),
                    acknowledged = await recipients.CountAsync(r => r.AcknowledgedAt != null, ct),
                };
            }

            return Ok(new
            {
                id                      = doc.Id,
                title                   = doc.Title,
                number                  = doc.Number,
                summary                 = doc.Summary,
                status                  = doc.Status,
                authorId                = doc.AuthorId,
                authorName              = doc.Author?.FullName ?? doc.Author?.Username ?? "—",
                authorUnitName          = doc.AuthorOrgUnitId is { } au ? tree.PathOf(au) : null,
                distributionMode        = doc.DistributionMode,
                includeSubunits         = doc.IncludeSubunits,
                requiresAcknowledgement = doc.RequiresAcknowledgement,
                targetUnits             = doc.Targets
                    .Where(t => t.Kind == DistributionTargetKind.Unit)
                    .Select(t => new { id = t.TargetId, name = tree.Find(t.TargetId) is null ? "(ștearsă)" : tree.PathOf(t.TargetId) }),
                targetUsers,
                fileName                = doc.FileName,
                fileSize                = doc.FileSize,
                sha256                  = doc.Sha256,
                createdAt               = doc.CreatedAt,
                updatedAt               = doc.UpdatedAt,
                publishedAt             = doc.PublishedAt,
                repealedAt              = doc.RepealedAt,
                repealedReason          = doc.RepealedReason,
                isAuthor,
                isRecipient             = mine is not null,
                myOpenedAt              = mine?.FirstOpenedAt,
                myAcknowledgedAt        = mine?.AcknowledgedAt,
                counts,
                canEdit                 = isAuthor && doc.Status == InternalDocumentStatus.Draft,
                canPublish              = isAuthor && doc.Status == InternalDocumentStatus.Draft,
                canDelete               = isAuthor && doc.Status == InternalDocumentStatus.Draft,
                canRepeal               = (isAuthor || IsAdministrator) && doc.Status == InternalDocumentStatus.Published,
                canAcknowledge          = mine is not null
                                          && doc.Status == InternalDocumentStatus.Published
                                          && doc.RequiresAcknowledgement
                                          && mine.AcknowledgedAt is null,
                canViewReport           = (isAuthor || IsAdministrator) && doc.Status != InternalDocumentStatus.Draft,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/InternalDocuments — ciornă nouă
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]
        public async Task<IActionResult> Create([FromForm] SaveInternalDocumentRequest request, CancellationToken ct)
        {
            if (request.File is null || request.File.Length == 0)
                return BadRequest(new { message = "Fișierul documentului este obligatoriu." });

            var error = ValidateFields(request);
            if (error is not null) return BadRequest(new { message = error });

            var (resolved, _, _) = await ResolveAsync(request.ToDistribution(), ct);
            if (!resolved.Success) return BadRequest(new { message = resolved.Error });

            var userId    = CurrentUserId;
            var authorUnit = await _context.Users.Where(u => u.Id == userId).Select(u => u.OrgUnitId).FirstOrDefaultAsync(ct);

            var doc = new InternalDocument
            {
                Title                   = request.Title!.Trim(),
                Number                  = Clean(request.Number, 64),
                Summary                 = Clean(request.Summary, 2000),
                AuthorId                = userId,
                AuthorOrgUnitId         = authorUnit,
                Status                  = InternalDocumentStatus.Draft,
                DistributionMode        = request.DistributionMode,
                IncludeSubunits         = request.IncludeSubunits,
                RequiresAcknowledgement = request.RequiresAcknowledgement,
                CreatedAt               = DateTime.UtcNow,
            };

            SetTargets(doc, request);

            var stored = await StoreFileAsync(doc.Id, request.File, ct);
            if (stored.Error is not null) return BadRequest(new { message = stored.Error });
            ApplyFile(doc, stored);

            _context.InternalDocuments.Add(doc);
            AddAudit(AuditAction.InternalDocumentCreated,
                $"Document intern creat (ciorna) '{doc.Title}' (id {doc.Id}), distributie {doc.DistributionMode}, " +
                $"{resolved.RecipientIds.Count} destinatari estimati");

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch
            {
                await TryDeleteObjectAsync(stored.Key!);
                throw;
            }

            return Ok(new { id = doc.Id, message = "Ciorna a fost salvată. Publicați-o când este gata de distribuit." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // PUT api/InternalDocuments/{id} — editarea ciornei
        // ═════════════════════════════════════════════════════════════════════
        [HttpPut("{id:guid}")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(UploadLimits.MaxRequestBytes)]
        public async Task<IActionResult> Update(Guid id, [FromForm] SaveInternalDocumentRequest request, CancellationToken ct)
        {
            var doc = await _context.InternalDocuments
                .Include(d => d.Targets)
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });
            if (doc.AuthorId != CurrentUserId) return Forbid();
            if (doc.Status != InternalDocumentStatus.Draft)
                return Conflict(new { message = "Doar ciornele se pot modifica. Un document publicat se abrogă și se emite altul." });

            var error = ValidateFields(request);
            if (error is not null) return BadRequest(new { message = error });

            var (resolved, _, _) = await ResolveAsync(request.ToDistribution(), ct);
            if (!resolved.Success) return BadRequest(new { message = resolved.Error });

            doc.Title                   = request.Title!.Trim();
            doc.Number                  = Clean(request.Number, 64);
            doc.Summary                 = Clean(request.Summary, 2000);
            doc.DistributionMode        = request.DistributionMode;
            doc.IncludeSubunits         = request.IncludeSubunits;
            doc.RequiresAcknowledgement = request.RequiresAcknowledgement;
            doc.UpdatedAt               = DateTime.UtcNow;

            _context.InternalDocumentTargets.RemoveRange(doc.Targets);
            doc.Targets.Clear();
            SetTargets(doc, request);

            string? oldKey = null;
            StoredFile? stored = null;
            if (request.File is { Length: > 0 })
            {
                stored = await StoreFileAsync(doc.Id, request.File, ct);
                if (stored.Error is not null) return BadRequest(new { message = stored.Error });
                oldKey = doc.StorageKey;
                ApplyFile(doc, stored);
            }

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch
            {
                if (stored?.Key is not null) await TryDeleteObjectAsync(stored.Key);
                throw;
            }

            // Fișierul vechi al ciornei nu mai e referit de nimic.
            if (!string.IsNullOrEmpty(oldKey)) await TryDeleteObjectAsync(oldKey);

            return Ok(new { message = "Ciorna a fost actualizată." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // DELETE api/InternalDocuments/{id} — doar ciorne
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// O ciornă nu a ajuns la nimeni, deci nu are valoare de dovadă și se
        /// poate șterge definitiv. Un document publicat nu se șterge niciodată —
        /// se abrogă.
        /// </summary>
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var doc = await _context.InternalDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });
            if (doc.AuthorId != CurrentUserId) return Forbid();
            if (doc.Status != InternalDocumentStatus.Draft)
                return Conflict(new { message = "Un document publicat nu se șterge. Abrogați-l." });

            _context.InternalDocuments.Remove(doc);
            await _context.SaveChangesAsync(ct);
            await TryDeleteObjectAsync(doc.StorageKey);

            return Ok(new { message = "Ciorna a fost ștearsă." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/InternalDocuments/{id}/publish
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Publică: distribuția se rezolvă din nou (structura s-a putut schimba de
        /// la salvarea ciornei), iar lista destinatarilor se fixează.
        /// </summary>
        [HttpPost("{id:guid}/publish")]
        public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
        {
            var doc = await _context.InternalDocuments
                .Include(d => d.Targets)
                .FirstOrDefaultAsync(d => d.Id == id, ct);

            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });
            if (doc.AuthorId != CurrentUserId) return Forbid();
            if (doc.Status != InternalDocumentStatus.Draft)
                return Conflict(new { message = "Documentul a fost deja publicat." });

            var input = new DistributionInput
            {
                Mode            = doc.DistributionMode,
                IncludeSubunits = doc.IncludeSubunits,
                UnitIds         = doc.Targets.Where(t => t.Kind == DistributionTargetKind.Unit).Select(t => t.TargetId).ToList(),
                UserIds         = doc.Targets.Where(t => t.Kind == DistributionTargetKind.User).Select(t => t.TargetId).ToList(),
            };

            var (resolved, _, members) = await ResolveAsync(input, ct);
            if (!resolved.Success) return BadRequest(new { message = resolved.Error });

            var now    = DateTime.UtcNow;
            var byUser = members.ToDictionary(m => m.Id);

            foreach (var userId in resolved.RecipientIds)
            {
                doc.Recipients.Add(new InternalDocumentRecipient
                {
                    DocumentId = doc.Id,
                    UserId     = userId,
                    OrgUnitId  = byUser.TryGetValue(userId, out var m) ? m.OrgUnitId : null,
                    AddedAt    = now,
                });
            }

            doc.Status      = InternalDocumentStatus.Published;
            doc.PublishedAt = now;

            AddAudit(AuditAction.InternalDocumentPublished,
                $"Document intern publicat '{doc.Title}' (id {doc.Id}) catre {resolved.RecipientIds.Count} destinatari, " +
                $"distributie {doc.DistributionMode}" +
                (doc.RequiresAcknowledgement ? ", cu confirmare de luare la cunostinta" : string.Empty));

            await _context.SaveChangesAsync(ct);

            return Ok(new
            {
                message    = $"Documentul a fost publicat către {resolved.RecipientIds.Count} destinatari.",
                recipients = resolved.RecipientIds.Count,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/InternalDocuments/{id}/repeal
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost("{id:guid}/repeal")]
        public async Task<IActionResult> Repeal(Guid id, [FromBody] RepealDto? dto, CancellationToken ct)
        {
            var doc = await _context.InternalDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });
            if (doc.AuthorId != CurrentUserId && !IsAdministrator) return Forbid();
            if (doc.Status != InternalDocumentStatus.Published)
                return Conflict(new { message = "Doar un document publicat poate fi abrogat." });

            doc.Status         = InternalDocumentStatus.Repealed;
            doc.RepealedAt     = DateTime.UtcNow;
            doc.RepealedById   = CurrentUserId;
            doc.RepealedReason = Clean(dto?.Reason, 500);

            AddAudit(AuditAction.InternalDocumentRepealed,
                $"Document intern abrogat '{doc.Title}' (id {doc.Id})" +
                (doc.RepealedReason is null ? string.Empty : $": {doc.RepealedReason}"),
                AuditResult.Warning);

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Documentul a fost abrogat. Rămâne vizibil destinatarilor, marcat ca abrogat." });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/InternalDocuments/{id}/download
        // ═════════════════════════════════════════════════════════════════════
        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;

            var doc = await _context.InternalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });

            var mine = await _context.InternalDocumentRecipients
                .FirstOrDefaultAsync(r => r.DocumentId == id && r.UserId == userId, ct);

            if (!CanRead(doc, mine)) return Forbid();

            Stream stream;
            try
            {
                stream = await _storage.OpenReadAsync(doc.StorageKey, ct);
            }
            catch (FileNotFoundException)
            {
                _logger.LogError("Document intern fara obiect in depozit: {Id} → {Key}", doc.Id, doc.StorageKey);
                return NotFound(new { message = "Fișierul nu mai există în depozit." });
            }

            // Prima deschidere a unui destinatar e condiția pentru „Luat la cunoștință”.
            if (mine is not null && mine.FirstOpenedAt is null && doc.Status != InternalDocumentStatus.Draft)
            {
                mine.FirstOpenedAt = DateTime.UtcNow;
                AddAudit(AuditAction.InternalDocumentOpened,
                    $"Document intern deschis prima data '{doc.Title}' (id {doc.Id})");
            }
            else
            {
                AddAudit(AuditAction.InternalDocumentOpened, $"Document intern descarcat '{doc.Title}' (id {doc.Id})");
            }
            await _context.SaveChangesAsync(ct);

            var contentType = SafeContentTypes.Contains(doc.ContentType) ? doc.ContentType : "application/octet-stream";
            return File(stream, contentType, doc.FileName);
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST api/InternalDocuments/{id}/acknowledge
        // ═════════════════════════════════════════════════════════════════════
        [HttpPost("{id:guid}/acknowledge")]
        public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
        {
            var userId = CurrentUserId;

            var recipient = await _context.InternalDocumentRecipients
                .Include(r => r.Document)
                .FirstOrDefaultAsync(r => r.DocumentId == id && r.UserId == userId, ct);

            if (recipient?.Document is null)
                return NotFound(new { message = "Documentul nu v-a fost distribuit." });

            var doc = recipient.Document;

            if (recipient.AcknowledgedAt.HasValue)
                return Ok(new { message = "Ați confirmat deja luarea la cunoștință.", acknowledgedAt = recipient.AcknowledgedAt });

            if (doc.Status == InternalDocumentStatus.Repealed)
                return Conflict(new { message = "Documentul a fost abrogat și nu mai cere confirmare." });

            if (!doc.RequiresAcknowledgement)
                return BadRequest(new { message = "Documentul nu cere confirmare de luare la cunoștință." });

            if (recipient.FirstOpenedAt is null)
                return Conflict(new { message = "Deschideți documentul înainte de a confirma că l-ați luat la cunoștință." });

            recipient.AcknowledgedAt = DateTime.UtcNow;

            AddAudit(AuditAction.InternalDocumentAcknowledged,
                $"Luat la cunostinta: '{doc.Title}' (id {doc.Id})");

            await _context.SaveChangesAsync(ct);
            return Ok(new { message = "Confirmarea a fost înregistrată.", acknowledgedAt = recipient.AcknowledgedAt });
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET api/InternalDocuments/{id}/report
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Raportul pentru autor: cine a deschis, cine a confirmat, pe
        /// subdiviziuni. Grupat după subdiviziunea destinatarului LA PUBLICARE.
        /// </summary>
        [HttpGet("{id:guid}/report")]
        public async Task<IActionResult> Report(Guid id, CancellationToken ct)
        {
            var doc = await _context.InternalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return NotFound(new { message = "Documentul nu a fost găsit." });
            if (doc.AuthorId != CurrentUserId && !IsAdministrator) return Forbid();

            var tree = await OrgStructure.LoadTreeAsync(_context, ct);

            var rows = await _context.InternalDocumentRecipients
                .AsNoTracking()
                .Where(r => r.DocumentId == id)
                .Select(r => new
                {
                    r.UserId,
                    Name     = r.User != null ? (r.User.FullName ?? r.User.Username) : "—",
                    Username = r.User != null ? r.User.Username : "",
                    IsActive = r.User != null && r.User.IsActive,
                    r.OrgUnitId,
                    r.FirstOpenedAt,
                    r.AcknowledgedAt,
                })
                .ToListAsync(ct);

            string UnitName(Guid? unitId) => unitId is { } u && tree.Find(u) is not null ? tree.PathOf(u) : "Neîncadrați";

            var recipients = rows
                .OrderBy(r => r.AcknowledgedAt.HasValue).ThenBy(r => UnitName(r.OrgUnitId)).ThenBy(r => r.Name)
                .Select(r => new
                {
                    userId         = r.UserId,
                    name           = r.Name,
                    username       = r.Username,
                    isActive       = r.IsActive,
                    orgUnitId      = r.OrgUnitId,
                    unitName       = UnitName(r.OrgUnitId),
                    openedAt       = r.FirstOpenedAt,
                    acknowledgedAt = r.AcknowledgedAt,
                });

            var byUnit = rows
                .GroupBy(r => r.OrgUnitId)
                .Select(g => new
                {
                    orgUnitId    = g.Key,
                    name         = UnitName(g.Key),
                    total        = g.Count(),
                    opened       = g.Count(r => r.FirstOpenedAt.HasValue),
                    acknowledged = g.Count(r => r.AcknowledgedAt.HasValue),
                })
                .OrderBy(x => x.name);

            return Ok(new
            {
                id                      = doc.Id,
                title                   = doc.Title,
                requiresAcknowledgement = doc.RequiresAcknowledgement,
                publishedAt             = doc.PublishedAt,
                total                   = rows.Count,
                opened                  = rows.Count(r => r.FirstOpenedAt.HasValue),
                acknowledged            = rows.Count(r => r.AcknowledgedAt.HasValue),
                byUnit,
                recipients,
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cine poate citi: autorul (inclusiv ciorna), destinatarii (după
        /// publicare) și administratorul (supervizare).
        /// </summary>
        private bool CanRead(InternalDocument doc, InternalDocumentRecipient? mine) =>
            doc.AuthorId == CurrentUserId
            || IsAdministrator
            || (mine is not null && doc.Status != InternalDocumentStatus.Draft);

        private async Task<(DistributionResult Result, OrgTree Tree, List<OrgMember> Members)> ResolveAsync(
            DistributionInput input, CancellationToken ct)
        {
            var tree    = await OrgStructure.LoadTreeAsync(_context, ct);
            var members = await OrgStructure.LoadMembersAsync(_context, ct);

            var result = DistributionResolver.Resolve(
                CurrentUserId,
                IsAdministrator,
                new DistributionRequest(
                    input.Mode,
                    (input.UnitIds ?? []).Distinct().Take(MaxTargets).ToList(),
                    (input.UserIds ?? []).Distinct().Take(MaxTargets).ToList(),
                    input.IncludeSubunits),
                tree,
                members);

            return (result, tree, members);
        }

        private static string? ValidateFields(SaveInternalDocumentRequest r)
        {
            if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Trim().Length > 300)
                return "Titlul este obligatoriu (maxim 300 de caractere).";
            if (!Enum.IsDefined(r.DistributionMode))
                return "Modul de distribuție nu există.";
            if ((r.UnitIds?.Count ?? 0) > MaxTargets || (r.UserIds?.Count ?? 0) > MaxTargets)
                return $"Maxim {MaxTargets} ținte de distribuție.";
            return null;
        }

        /// <summary>Se memorează doar țintele relevante modului ales.</summary>
        private static void SetTargets(InternalDocument doc, SaveInternalDocumentRequest r)
        {
            List<Guid> units = r.DistributionMode == DistributionMode.SelectedUnits ? (r.UnitIds ?? []) : [];
            List<Guid> users = r.DistributionMode == DistributionMode.SpecificUsers ? (r.UserIds ?? []) : [];

            foreach (var id in units.Distinct())
                doc.Targets.Add(new InternalDocumentTarget { DocumentId = doc.Id, Kind = DistributionTargetKind.Unit, TargetId = id });
            foreach (var id in users.Distinct())
                doc.Targets.Add(new InternalDocumentTarget { DocumentId = doc.Id, Kind = DistributionTargetKind.User, TargetId = id });
        }

        private sealed record StoredFile(string? Key, string? FileName, string? ContentType, long Size, string? Sha256, string? Error);

        private async Task<StoredFile> StoreFileAsync(Guid docId, IFormFile file, CancellationToken ct)
        {
            if (file.Length > _storageOptions.MaxFileSizeBytes)
                return new(null, null, null, 0, null, $"Fișierul depășește limita de {_storageOptions.MaxFileSizeMb} MB.");

            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName))
                return new(null, null, null, 0, null, "Numele fișierului este invalid.");
            if (safeName.Length > 260) safeName = safeName[..260];

            var ext = Path.GetExtension(safeName).ToLowerInvariant();
            if (ext.Length > 10 || ext.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) ext = string.Empty;

            var now = DateTime.UtcNow;
            var key = $"{StoragePrefix}/{now:yyyy}/{now:MM}/{docId:N}-{Guid.NewGuid():N}{ext}";

            await using var stream = file.OpenReadStream();

            string sha;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81_920];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    hasher.AppendData(buffer, 0, read);
                sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            stream.Position = 0;

            var contentType = string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType.Length > 128
                ? "application/octet-stream"
                : file.ContentType;

            await _storage.PutAsync(key, stream, file.Length, contentType, ct);
            return new(key, safeName, contentType, file.Length, sha, null);
        }

        private static void ApplyFile(InternalDocument doc, StoredFile f)
        {
            doc.StorageKey  = f.Key!;
            doc.FileName    = f.FileName!;
            doc.ContentType = f.ContentType!;
            doc.FileSize    = f.Size;
            doc.Sha256      = f.Sha256!;
        }

        private async Task TryDeleteObjectAsync(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            try { await _storage.DeleteAsync(key, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Obiectul {Key} nu a putut fi șters din depozit.", key); }
        }

        private static string? Clean(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            return v.Length <= max ? v : v[..max];
        }

        private void AddAudit(AuditAction action, string details, AuditResult result = AuditResult.Success) =>
            _context.AuditLogs.Add(new AuditLog
            {
                UserId    = CurrentUserId,
                Username  = CurrentUsername,
                Action    = action,
                Details   = details,
                Result    = result,
                IpAddress = CallerIp,
                Timestamp = DateTime.UtcNow,
            });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Modele
    // ═════════════════════════════════════════════════════════════════════════

    public class DistributionInput
    {
        public DistributionMode Mode { get; set; }
        public List<Guid>? UnitIds { get; set; }
        public List<Guid>? UserIds { get; set; }
        public bool IncludeSubunits { get; set; } = true;
    }

    public class SaveInternalDocumentRequest
    {
        public IFormFile? File { get; set; }
        public string? Title { get; set; }
        public string? Number { get; set; }
        public string? Summary { get; set; }
        public DistributionMode DistributionMode { get; set; } = DistributionMode.SpecificUsers;
        public bool IncludeSubunits { get; set; } = true;
        public bool RequiresAcknowledgement { get; set; } = true;
        public List<Guid>? UnitIds { get; set; }
        public List<Guid>? UserIds { get; set; }

        public DistributionInput ToDistribution() => new()
        {
            Mode            = DistributionMode,
            UnitIds         = UnitIds,
            UserIds         = UserIds,
            IncludeSubunits = IncludeSubunits,
        };
    }

    public class RepealDto
    {
        public string? Reason { get; set; }
    }

    public class InternalDocumentListItem
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Number { get; set; }
        public string? Summary { get; set; }
        public InternalDocumentStatus Status { get; set; }
        public DistributionMode DistributionMode { get; set; }
        public bool RequiresAcknowledgement { get; set; }
        public Guid AuthorId { get; set; }
        public string AuthorName { get; set; } = string.Empty;
        public string? AuthorUnitName { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DateTime? RepealedAt { get; set; }
        public bool IsAuthor { get; set; }
        public DateTime? MyOpenedAt { get; set; }
        public DateTime? MyAcknowledgedAt { get; set; }
        public int? RecipientCount { get; set; }
        public int? OpenedCount { get; set; }
        public int? AcknowledgedCount { get; set; }
    }
}
