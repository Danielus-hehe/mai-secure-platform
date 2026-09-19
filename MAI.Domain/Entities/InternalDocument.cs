using System;
using System.Collections.Generic;
using MAI.Domain.Enums;

namespace MAI.Domain.Entities
{
    /// <summary>
    /// Document intern distribuit pe structura organizatorică: dispoziții,
    /// note, circulare - cu confirmare de luare la cunoștință.
    ///
    /// Diferit de registrul de documente normative (Document), care e public în
    /// toată instituția, și de transferuri, care sunt criptate end-to-end între
    /// persoane. Un document intern NU e criptat end-to-end, deliberat:
    /// distribuția „subdiviziunea mea” se rezolvă pe server, după structura din
    /// momentul publicării, iar serverul trebuie să poată decide singur cine are
    /// acces. Cu E2EE, fiecare destinatar ar avea nevoie de chei generate înainte
    /// de publicare, iar un coleg transferat ulterior în subdiviziune nu ar putea
    /// primi niciodată acces. Protecția vine din controlul accesului pe server,
    /// criptarea depozitului și jurnalul de audit.
    /// </summary>
    public class InternalDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Title { get; set; } = string.Empty;

        /// <summary>Numărul de înregistrare, ex. „D-2026/0142”. Opțional.</summary>
        public string? Number { get; set; }

        /// <summary>Rezumat scurt, afișat în listă.</summary>
        public string? Summary { get; set; }

        public Guid AuthorId { get; set; }
        public User? Author { get; set; }

        /// <summary>Subdiviziunea autorului în momentul creării (instantaneu, pentru raport).</summary>
        public Guid? AuthorOrgUnitId { get; set; }
        public OrgUnit? AuthorOrgUnit { get; set; }

        public InternalDocumentStatus Status { get; set; } = InternalDocumentStatus.Draft;

        // ── Distribuție ──────────────────────────────────────────────────────

        public DistributionMode DistributionMode { get; set; } = DistributionMode.SpecificUsers;

        /// <summary>La SelectedUnits: se includ și subunitățile celor alese.</summary>
        public bool IncludeSubunits { get; set; } = true;

        /// <summary>Destinatarii trebuie să confirme „Luat la cunoștință”.</summary>
        public bool RequiresAcknowledgement { get; set; } = true;

        /// <summary>Subdiviziunile / persoanele alese de autor (după mod).</summary>
        public ICollection<InternalDocumentTarget> Targets { get; set; } = [];

        /// <summary>Lista fixată la publicare, cu starea fiecărui destinatar.</summary>
        public ICollection<InternalDocumentRecipient> Recipients { get; set; } = [];

        // ── Fișierul ─────────────────────────────────────────────────────────

        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public long FileSize { get; set; }
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>Cheia din depozit: internal/{yyyy}/{MM}/{id}{ext}.</summary>
        public string StorageKey { get; set; } = string.Empty;

        // ── Istoric ──────────────────────────────────────────────────────────

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DateTime? RepealedAt { get; set; }
        public Guid? RepealedById { get; set; }
        public string? RepealedReason { get; set; }
    }

    /// <summary>O țintă aleasă de autor: o subdiviziune sau o persoană.</summary>
    public class InternalDocumentTarget
    {
        public Guid DocumentId { get; set; }
        public InternalDocument? Document { get; set; }

        public DistributionTargetKind Kind { get; set; }

        /// <summary>Id-ul subdiviziunii sau al utilizatorului, după Kind.</summary>
        public Guid TargetId { get; set; }
    }

    /// <summary>
    /// Un destinatar al unui document publicat, cu dovada lecturii și a luării
    /// la cunoștință. Lista se fixează la publicare: cine intră ulterior în
    /// subdiviziune nu apare aici, cine pleacă rămâne.
    /// </summary>
    public class InternalDocumentRecipient
    {
        public Guid DocumentId { get; set; }
        public InternalDocument? Document { get; set; }

        public Guid UserId { get; set; }
        public User? User { get; set; }

        /// <summary>
        /// Subdiviziunea destinatarului la publicare. Raportul pentru autor
        /// grupează pe ea, nu pe subdiviziunea curentă: un transfer de personal
        /// ulterior nu mută confirmarea în alt rând al raportului.
        /// </summary>
        public Guid? OrgUnitId { get; set; }
        public OrgUnit? OrgUnit { get; set; }

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Prima deschidere a documentului.</summary>
        public DateTime? FirstOpenedAt { get; set; }

        /// <summary>
        /// Confirmarea „Luat la cunoștință”. Posibilă doar după deschidere:
        /// confirmarea unui document necitit nu dovedește nimic.
        /// </summary>
        public DateTime? AcknowledgedAt { get; set; }
    }
}
