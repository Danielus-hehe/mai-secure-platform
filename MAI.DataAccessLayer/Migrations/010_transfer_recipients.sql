-- ─────────────────────────────────────────────────────────────────────────────
-- Migrare 010 — Destinatari suplimentari (Feature #5: forward / multi-share)
--
-- Rulează manual în Supabase SQL Editor sau cu psql:
--   psql $DATABASE_URL -f 010_transfer_recipients.sql
--
-- Idempotent: IF NOT EXISTS pe tabelă și indecși — poate fi rulat de mai multe
-- ori fără efecte secundare.
-- ─────────────────────────────────────────────────────────────────────────────

BEGIN;

-- ── Tabelă ───────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "TransferRecipients" (
    -- Cheie primară compusă: același utilizator nu poate apărea de două ori
    -- ca destinatar al aceluiași transfer.
    "TransferId"          uuid        NOT NULL,
    "UserId"              uuid        NOT NULL,

    -- DEK-ul transferului împachetat cu cheia publică RSA-OAEP a lui UserId.
    -- RSA-3072 → 384 octeți → ~512 caractere base64. Marja de 600 ≡
    -- "FileTransfers"."EncryptedKeyForRecipient".
    "EncryptedKeyForUser" varchar(600) NOT NULL,

    -- Cine a inițiat forward-ul. Null = câmp rezervat pentru audit viitor.
    "ForwardedById"       uuid        NULL,

    -- Timestamp cu fus orar, setat automat la INSERT.
    "SentAt"              timestamptz NOT NULL DEFAULT NOW(),

    CONSTRAINT "PK_TransferRecipients"
        PRIMARY KEY ("TransferId", "UserId"),

    -- Dacă transferul e șters fizic, destinatarii dispar și ei.
    CONSTRAINT "FK_TransferRecipients_FileTransfers_TransferId"
        FOREIGN KEY ("TransferId")
        REFERENCES "FileTransfers" ("Id")
        ON DELETE CASCADE,

    -- Dacă utilizatorul-destinatar e șters, rândul rămâne (RESTRICT) —
    -- ștergerea unui cont nu trebuie să ascundă faptul că a primit documente.
    CONSTRAINT "FK_TransferRecipients_Users_UserId"
        FOREIGN KEY ("UserId")
        REFERENCES "Users" ("Id")
        ON DELETE RESTRICT,

    -- Dacă utilizatorul care a făcut forward e șters, câmpul devine null —
    -- nu pierdem rândul destinatarului din cauza asta.
    CONSTRAINT "FK_TransferRecipients_Users_ForwardedById"
        FOREIGN KEY ("ForwardedById")
        REFERENCES "Users" ("Id")
        ON DELETE SET NULL
);

-- ── Indecși ───────────────────────────────────────────────────────────────────

-- GetAll: "există vreun rând cu UserId == currentUser?" la fiecare pagină.
CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_UserId"
    ON "TransferRecipients" ("UserId");

-- Rapid la ștergerea în cascadă și la listarea destinatarilor unui transfer.
CREATE INDEX IF NOT EXISTS "IX_TransferRecipients_TransferId"
    ON "TransferRecipients" ("TransferId");

COMMIT;
