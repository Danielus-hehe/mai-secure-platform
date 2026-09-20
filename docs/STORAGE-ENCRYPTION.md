# Criptarea depozitului (documente normative și interne)

## Ce se criptează și de ce

| Prefix în MinIO | Conținut | Cine criptează |
|---|---|---|
| `transfers/` | transferuri între persoane | browserul (E2EE, AES-256-GCM + RSA-OAEP) |
| `documents/` | documente normative, cu versiuni | API-ul, înainte de MinIO |
| `internal/` | documente interne distribuite pe structură | API-ul, înainte de MinIO |

Documentele normative și interne nu pot fi E2EE: serverul decide cine le primește
(distribuția pe subdiviziuni se rezolvă la publicare) și caută după titlu. Fără
criptarea de aici, oricine are credențialele MinIO, o copie a volumului sau un
backup le citește în clar.

Ce apără: dump de volum MinIO, backup pierdut, credențiale MinIO scurse,
administrator de stocare. Ce NU apără: un atacator care controlează procesul API
(are cheile în memorie). Pentru acel scenariu există E2EE, folosit la transferuri.

## Cum funcționează

- Fiecare fișier primește o cheie proprie AES-256 (DEK), aleatorie.
- DEK se împachetează (AES-256-GCM) cu o **cheie principală** din `.env`
  (`MAI_STORAGE_MASTER_KEYS`), identificată printr-un id scris în antetul fișierului.
- Conținutul se criptează în segmente de 64 KiB, fiecare autentificat separat.
  Nonce-ul segmentului conține numărul de ordine și marcajul „ultimul segment”:
  modificarea, reordonarea sau trunchierea fișierului sunt detectate.
- La descărcare, API-ul verifică întâi tot fișierul, apoi îl livrează. Un fișier
  alterat nu trimite niciun octet spre browser: răspunsul este 500 cu mesaj
  explicit, iar jurnalul de audit primește `StorageIntegrityFailure` (categoria
  Securitate).
- Amprenta SHA-256 din registru rămâne cea a documentului original, deci
  verificarea din browser (pagina Documente) funcționează ca înainte.
- URL-urile presemnate nu se emit pentru `documents/` și `internal/`: browserul
  nu ar putea decripta cifrotextul.

Formatul exact este descris în `MAI.BusinessLogic/Storage/Encryption/StorageEnvelope.cs`.

## Configurare (.env)

```
MAI_STORAGE_MASTER_KEYS=k1:<32 de octeti base64>
STORAGE_ENCRYPTION_ACTIVE_KEY=k1
STORAGE_ENCRYPTION_ENABLED=true
STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=true
```

Generarea unei chei (afișează liniile gata completate):

```
dotnet run --project MAI.Api -- storage:generate-key k1
```

Cu `STORAGE_ENCRYPTION_ENABLED=true` și fără cheie, API-ul refuză să pornească,
cu mesaj explicit. **Păstrați o copie a cheilor în afara serverului**: fără ele,
documentele criptate nu mai pot fi citite de nimeni.

## Prima activare (fișiere scrise înainte de criptare)

Fișierele vechi rămân citibile (`STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=true`). Pentru
a le cripta:

```
dotnet run --project MAI.Api -- storage:recrypt --dry-run
dotnet run --project MAI.Api -- storage:recrypt
```

În Docker: `docker compose run --rm api storage:recrypt --dry-run`, apoi fără `--dry-run`.

Pentru fiecare fișier din `documents/` și `internal/` referit în baza de date:

- în clar → criptat, **doar dacă** SHA-256 al conținutului corespunde celui din
  baza de date; altfel e raportat `AMPRENTA DIFERITA` și lăsat neatins;
- după scriere, fișierul se recitește, se decriptează complet și se verifică din nou;
- rularea este idempotentă; la final se scrie o intrare în jurnalul de audit.

După o rulare fără probleme: `STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=false` și repornire.
De atunci, un fișier în clar pus direct în MinIO în locul celui criptat este refuzat.

## Rotirea cheii principale

1. Generați o cheie nouă: `dotnet run --project MAI.Api -- storage:generate-key k2`
2. În `.env`, **adăugați-o** după cea veche și mutați cheia activă:
   `MAI_STORAGE_MASTER_KEYS=k1:...;k2:...` și `STORAGE_ENCRYPTION_ACTIVE_KEY=k2`
3. Reporniți API-ul (fișierele noi se criptează cu k2, cele vechi se citesc cu k1).
4. `storage:recrypt`: pentru fișierele pe k1 se rescrie doar antetul (DEK
   re-împachetat cu k2); conținutul nu se decriptează.
5. Când o nouă rulare `--dry-run` nu mai arată nimic de re-împachetat, scoateți
   k1 din `.env`. Versiunile vechi din bucket (versionare MinIO) expiră după o zi;
   după scoaterea cheii, ele devin ilizibile - exact efectul dorit la o cheie compromisă.

## Demonstrație (eșec controlat)

1. Publicați un document normativ.
2. În consola MinIO (http://localhost:9001), descărcați obiectul din `documents/`:
   începe cu `SGDMENC`, conținutul nu se poate citi.
3. Modificați un octet din obiect și încărcați-l la loc (sau înlocuiți-l cu
   un fișier oarecare, cu `STORAGE_ENCRYPTION_ALLOW_PLAINTEXT=false`).
4. Descărcarea din aplicație este refuzată, iar în Jurnal apare
   „Descarcare refuzata ... verificarea de integritate”.
