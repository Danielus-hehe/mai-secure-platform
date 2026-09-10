# Migrări de bază de date

Schema SGDM se administrează prin fișierele SQL din acest folder, rulate manual
în ordine. **Nu** prin `dotnet ef database update` — vezi „Despre
`__EFMigrationsHistory`" mai jos.

---

## Care fișier, când

| Situație | Ce rulezi |
|---|---|
| Bază nouă, goală (Docker, alt mediu, alt coleg) | `000_baseline.sql`, apoi `007` |
| Baza de producție existentă (Supabase) | Doar `007` — restul sunt deja aplicate |
| Vrei date pentru demonstrație | `900_seed_demo.sql`, la final |

`001`–`006` sunt **istoric**. Arată ce s-a schimbat și în ce ordine, dar nu se
rulează pe o bază creată din baseline. Sunt idempotente, deci n-ar strica nimic —
doar n-ar face nimic.

---

## Starea la data generării

Verificată direct în Supabase, prin `information_schema` și `pg_indexes`.

| # | Fișier | Ce aduce | Aplicată |
|---|---|---|---|
| 000 | `000_baseline.sql` | Schema completă, echivalentă cu 001–006 | — |
| 001 | `001_refresh_token_columns.sql` | Refresh token cu rotație | da |
| 002 | `002_lockout_columns.sql` | Blocare progresivă a contului | da |
| 003 | `003_e2e_encryption.sql` | Chei E2EE + plic criptografic | da |
| 004 | `004_transfers_storage.sql` | MinIO: `StorageKey`, indexuri | da |
| 005 | `005_two_factor.sql` | TOTP, coduri de recuperare, provocare | da |
| 006 | `006_audit_result.sql` | `AuditResult` ca enum + indexuri | da |
| 007 | `007_sessions_and_revocation.sql` | `UserSessions`, retragere, dovadă de primire | **nu** |
| 900 | `900_seed_demo.sql` | Date demonstrative | opțional |

### Note despre reconstituire

`001`–`005` au fost **reconstituite** din conversațiile în care au fost scrise,
pentru că fișierele originale nu erau versionate. Corespund schemei reale, dar
comentariile originale s-au pierdut parțial.

`004` se numea inițial `003_transfers_storage.sql` și `006` se numea
`004_audit_result.sql`. Renumerotate aici, fiindcă numerele erau deja folosite.
Redenumirea unui fișier nu reaplică migrarea — ce e în bază rămâne în bază.

`000_baseline.sql` este generat din schema reală, deci este **autoritativ**.
Unde diferă de 001–006, baseline-ul are dreptate.

---

## Probleme deschise, găsite la verificarea schemei

### `Users.Username` nu are unicitate și nici index

Cea mai importantă. Baza de producție are pe `Users` un singur index: cheia
primară pe `Id`.

Două consecințe. Login-ul face `WHERE "Username" = ...`, deci fiecare
autentificare scanează toată tabela. La zeci de utilizatori nu se simte; la mii,
da. Mai grav: **nimic din baza de date nu împiedică două conturi cu același
`Username`**. Dacă se întâmplă, `FirstOrDefaultAsync` returnează unul dintre ele
nedeterminist, iar rezultatul e un utilizator care se autentifică uneori pe
contul altcuiva.

`000_baseline.sql` include indexul corect, dar el nu există în producție. De
aplicat separat, după verificare:

```sql
-- 1. Există duplicate? Dacă întoarce rânduri, NU crea indexul —
--    rezolvă întâi duplicatele.
SELECT lower("Username"), count(*)
  FROM "Users" GROUP BY 1 HAVING count(*) > 1;

SELECT lower("Email"), count(*)
  FROM "Users" GROUP BY 1 HAVING count(*) > 1;

-- 2. Dacă ambele sunt goale:
CREATE UNIQUE INDEX CONCURRENTLY "UX_Users_Username" ON "Users" ("Username");
CREATE UNIQUE INDEX CONCURRENTLY "UX_Users_Email"    ON "Users" ("Email");
```

`CONCURRENTLY` nu blochează tabela, dar **nu poate rula într-o tranzacție** —
deci se execută singur, nu în interiorul unui `BEGIN`.

### `IX_Users_RefreshTokenHash` lipsește din producție

Migrarea 001 îl creează, dar nu apare în `pg_indexes`. Ori nu a fost rulată
integral, ori indexul a fost șters ulterior. Efectul: fiecare `/refresh`
scanează tabela.

Devine irelevant după `007`, care mută căutarea pe `UserSessions` — unde indexul
unic pe hash este creat de migrare. Nu-l adăuga acum; aplică `007`.

### `FileTransfers.EncryptedStoragePath` e cod mort

Coloană rămasă dinainte de MinIO. Nimic din codul actual nu o scrie. Se poate
elimina, dar abia după ce confirmi că nu mai există rânduri istorice care depind
de ea:

```sql
SELECT count(*) FROM "FileTransfers"
 WHERE "EncryptedStoragePath" IS NOT NULL AND "StorageKey" = '';
```

### Coloanele de refresh token de pe `Users`

`007` le migrează în `UserSessions`, dar **nu le șterge**. Intenționat: dacă
apare o problemă și trebuie rollback la versiunea anterioară a API-ului, aceasta
are nevoie de ele. Se elimină într-o migrare separată, după ce noua versiune a
rulat stabil câteva zile.

---

## Despre `__EFMigrationsHistory`

Tabela există în bază, creată de EF Core la un moment dat, dar este
**nefolosită**. Schema se administrează prin fișierele de aici.

Nu se pot folosi amândouă. Dacă vreodată treci pe migrări EF, tabela devine
sursa de adevăr, iar fișierele din acest folder trebuie abandonate — altfel EF
va încerca să aplice modificări care există deja și va eșua.

---

## Cum rulezi

**Supabase:** Dashboard → SQL Editor → New query → lipești conținutul → Run.

**Postgres local sau Docker:**

```bash
psql "$CONNECTION_STRING" -f migrations/007_sessions_and_revocation.sql
```

Toate sunt idempotente (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`), deci o
rulare accidentală de două ori nu strică nimic.

---

## Următoarea migrare

Se numește `008_`. Reguli:

- Idempotentă. `IF NOT EXISTS` peste tot.
- Într-o singură tranzacție, cu excepția `CREATE INDEX CONCURRENTLY`.
- Cu interogările de verificare în comentariu, la final.
- **Verifică întâi ce există în bază.** Un `CREATE INDEX IF NOT EXISTS` cu alt
  nume decât un index existent pe aceleași coloane nu prinde duplicatul și
  creează un al doilea, plătit la fiecare scriere. S-a întâmplat deja o dată,
  între 004 și 007.
