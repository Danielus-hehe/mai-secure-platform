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
  Workflow-ul CI verifică perechile la fiecare push.
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
