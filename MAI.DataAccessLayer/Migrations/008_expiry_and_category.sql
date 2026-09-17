-- ============================================================
-- 008_expiry_and_category.sql
-- Adaugă ExpiresAt (nullable) și Category (int) pe FileTransfers
-- ============================================================
-- Categorii:  0=Critical  1=Important  2=General(default)  3=Normal
-- ============================================================

BEGIN;

ALTER TABLE "FileTransfers"
    ADD COLUMN IF NOT EXISTS "ExpiresAt"  timestamptz  NULL,
    ADD COLUMN IF NOT EXISTS "Category"   integer      NOT NULL DEFAULT 2;

CREATE INDEX IF NOT EXISTS "IX_FileTransfers_ExpiresAt"
    ON "FileTransfers" ("ExpiresAt")
    WHERE "ExpiresAt" IS NOT NULL;

COMMIT;