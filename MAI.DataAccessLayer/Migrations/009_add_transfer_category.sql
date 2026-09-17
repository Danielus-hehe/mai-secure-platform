-- ============================================================
-- 009_add_transfer_category.sql
-- Adaugă coloana Category pe FileTransfers.
-- NOTĂ: ExpiresAt există deja din migrarea anterioară.
-- ============================================================
-- Categorii:  0=Critical  1=Important  2=General(default)  3=Normal
-- ============================================================

BEGIN;

ALTER TABLE "FileTransfers"
    ADD COLUMN IF NOT EXISTS "Category" integer NOT NULL DEFAULT 2;

COMMIT;