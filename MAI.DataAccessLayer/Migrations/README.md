# Migrări de bază de date

Schema SGDM se administrează prin **migrări EF Core** din acest folder. Tabela
`__EFMigrationsHistory` din bază e sursa de adevăr: EF aplică doar migrările
care nu apar încă în ea.

> Versiunea anterioară a acestui fișier descria scripturi SQL numerotate
> (`000_baseline.sql` … `007_…sql`) și spunea că migrările EF nu se folosesc.
> Scripturile nu mai există în repository, iar proiectul a trecut complet pe
> migrări EF. Singurul SQL rămas aici este `900_seed_demo.sql`.

---

## Cum aplici migrările

Din rădăcina soluției:

```bash
dotnet ef database update --project MAI.DataAccessLayer --startup-project MAI.Api
```

Pentru un mediu unde nu vrei să rulezi `dotnet ef` direct (de exemplu Supabase,
din SQL Editor), generează un script idempotent și rulează-l acolo:

```bash
dotnet ef migrations script --idempotent \
  --project MAI.DataAccessLayer --startup-project MAI.Api \
  --output migrare.sql
```

Scriptul idempotent verifică `__EFMigrationsHistory` înaintea fiecărei migrări,
deci îl poți rula pe o bază parțial actualizată fără să se repete nimic.

---

## Lista migrărilor

| Migrare | Ce aduce |
|---|---|
| `20260906181449_InitialSupabase` | Schema inițială |
| `20260906183425_MakeFieldsOptional` | `FullName` și `Department` devin opționale |
| `20260907212431_AddRefreshToken` | Refresh token (prima variantă, pe `Users`) |
| `20260907215548_AddRefreshTokenAndLockout` | Blocare progresivă: încercări eșuate, ultimul login |
| `20260907223135_AddE2eeKeyColumns` | Chei E2EE + plicul criptografic al transferului |
| `20260908181941_AddTwoFactorAuthentication` | TOTP, coduri de recuperare, provocarea 2FA |
| `20260909155033_AddAuditResultAndIndexes` | `AuditResult` + indexurile jurnalului |
| `20260909205811_AddUserSessionsAndTransferRevocation` | `UserSessions`, retragere, dovadă de primire |
| `20260909210242_AddUserUniqueIndexes` | Unicitate pe `Username` și `Email` |
| `20260910200000_AddMustChangePassword` | Parolă temporară după creare/resetare de către admin |
| `20260910200100_CaseInsensitiveUserIndexes` | Unicitate fără diferență de majuscule; email opțional |
| `20260911090000_AddTotpReplayProtection` | Un cod TOTP nu mai poate fi folosit de două ori |
| `20260918120000_AddCategoryRecipientsInvitation` | Categoria transferului, `TransferRecipients` (forward), invitația de activare (`EmailConfirmed`, `InvitationToken`) |
| `20260919120000_PerRecipientReceiptsAndSoftDelete` | Dovada de primire per destinatar (`TransferRecipients.DownloadedAt/SignatureValid`), eliminarea destinatarului unic de pe `FileTransfers`, `AllowForward`, ștergere logică (`DeletedAt`, `DeletedById`), repararea stărilor suprascrise de jobul de expirare |

---

## `PerRecipientReceiptsAndSoftDelete` — înainte de aplicare

Migrarea **elimină coloane** din `FileTransfers` (`RecipientId`,
`EncryptedKeyForRecipient`, `DownloadedAt`, `RecipientSignatureValid`), după ce
le copiază conținutul în `TransferRecipients`. Faceți un backup înainte
(Supabase → Database → Backups, sau `pg_dump`).

Ce face, în ordine:

1. adaugă `DownloadedAt` și `SignatureValid` pe `TransferRecipients`;
2. copiază destinatarul original al fiecărui transfer ca rând în
   `TransferRecipients` (`ForwardedById = NULL` = destinatar direct);
3. golește `StorageKey` pe rândurile `Expired` (obiectul fusese deja șters) și
   reface stările suprascrise de jobul vechi: `Expired` cu `RevokedAt` → `Revoked`,
   `Expired` descărcat de toți → `Downloaded`; un `Downloaded` cu destinatari de
   forward care nu l-au deschis revine în `Pending`;
4. elimină coloanele vechi (cheia străină și indexul lor dispar odată cu ele);
5. adaugă `AllowForward` (TRUE pentru transferurile existente — așa se comportau
   —, FALSE implicit pentru cele noi), `DeletedAt`, `DeletedById`;
6. înlocuiește indexul `IX_TransferRecipients_UserId` cu
   `IX_TransferRecipients_UserId_DownloadedAt`.

**Limitare pentru datele vechi:** până la această migrare, confirmarea oricărui
destinatar (inclusiv a celor de forward) se scria pe rândul transferului. Nu se
mai poate afla cine a confirmat, deci confirmarea se atribuie destinatarului
original.

Verificare după aplicare:

```sql
-- Fiecare transfer are cel puțin un destinatar direct.
SELECT t."Id", t."FileName" FROM "FileTransfers" t
WHERE NOT EXISTS (SELECT 1 FROM "TransferRecipients" r
                  WHERE r."TransferId" = t."Id" AND r."ForwardedById" IS NULL);
```

---

## `AddCategoryRecipientsInvitation` — dacă ai rulat scripturile 008–010

Migrarea înlocuiește patru migrări scrise fără `.Designer.cs`
(`AddExpiryAndCategory`, `AddTransferCategory`, `AddTransferRecipients`,
`AddInvitationToken`) și scripturile SQL manuale `008`, `009`, `010`. EF nu le
descoperea, deci nu apar în `__EFMigrationsHistory` și nu trebuie șterse de acolo.

SQL-ul ei e idempotent (`IF NOT EXISTS`), deci rulează corect indiferent dacă
scripturile manuale au fost aplicate în Supabase sau nu. Coloana
`EmailConfirmed` e tratată separat: conturile existente sunt marcate confirmate
**doar** dacă migrarea creează coloana. Dacă ai adăugat-o deja de mână, verifică
după aplicare:

```sql
-- Conturile vechi trebuie să fie confirmate; altfel nu se pot autentifica.
SELECT "Username", "EmailConfirmed" FROM "Users" WHERE NOT "EmailConfirmed";
```

---

## Înainte de `CaseInsensitiveUserIndexes`

Migrarea creează două indexuri unice pe expresii:

```sql
UX_Users_Username_Lower  UNIQUE (lower("Username"))
UX_Users_Email_Lower     UNIQUE (lower("Email")) WHERE "Email" <> ''
```

Dacă în bază există deja două conturi care diferă doar prin majuscule
(`Ion.Popescu` și `ion.popescu`), crearea indexului eșuează și **migrarea nu se
aplică deloc** (rulează într-o tranzacție). Verifică întâi:

```sql
-- Trebuie să nu întoarcă niciun rând.
SELECT lower("Username"), count(*)
  FROM "Users" GROUP BY 1 HAVING count(*) > 1;

-- Emailurile goale sunt permise de mai multe ori; doar cele completate contează.
SELECT lower("Email"), count(*)
  FROM "Users" WHERE "Email" <> ''
 GROUP BY 1 HAVING count(*) > 1;
```

Dacă apar rânduri, redenumește sau dezactivează conturile duplicate înainte de
`dotnet ef database update`.

Indexurile pe expresii **nu apar** în `AppDbContextModelSnapshot.cs`: EF Core nu
le poate descrie în model. E normal. `dotnet ef migrations add` nu le va propune
spre ștergere, pentru că nu le vede.

---

## Reguli pentru migrările noi

- **Fiecare migrare are și fișierul `.Designer.cs`.** Fără el, EF Core nu
  descoperă migrarea și nu o aplică niciodată, deși proiectul compilează.
  Workflow-ul CI (`.github/workflows/ci.yml`, în rădăcina repository-ului)
  verifică perechile și unicitatea identificatorilor la fiecare push, iar
  `MigrationsTests` verifică același lucru prin reflecție.
- **Fără scripturi SQL paralele.** O modificare de schemă se face doar prin
  migrare EF. Scripturile manuale 008–010 au produs exact desincronizarea pe
  care a trebuit s-o repare `AddCategoryRecipientsInvitation`.
- **Generează-le cu `dotnet ef migrations add <Nume>`**, nu de mână, ca
  snapshot-ul să rămână sincron cu modelul.
- **Verifică întâi ce există în bază.** Un index creat cu alt nume decât unul
  existent pe aceleași coloane nu înlocuiește duplicatul: se plătește la fiecare
  scriere de două ori.
- Coloanele noi pe tabele cu date primesc o valoare implicită
  (`defaultValue`), altfel migrarea eșuează pe rândurile existente.

---

## Date demonstrative

`900_seed_demo.sql` creează conturi și documente pentru demonstrație. Se rulează
**după** migrări, manual (Supabase: SQL Editor; local: `psql -f`). Nu face parte
din migrările EF și nu trebuie rulat pe o bază cu date reale.

---

## Datorie tehnică cunoscută

- **Coloanele vechi de refresh token de pe `Users`** (`RefreshTokenHash`,
  `RefreshTokenIssuedAt`, `RefreshTokenExpiresAt`) nu mai sunt citite de cod:
  sesiunile stau în `UserSessions`. Au rămas intenționat, pentru un eventual
  rollback. Se elimină într-o migrare separată.
- **`FileTransfers.EncryptedStoragePath`** e rămasă dinainte de MinIO. Înainte
  de ștergere, confirmă că nu există rânduri istorice care depind de ea:

  ```sql
  SELECT count(*) FROM "FileTransfers"
   WHERE "EncryptedStoragePath" IS NOT NULL AND "StorageKey" = '';
  ```
