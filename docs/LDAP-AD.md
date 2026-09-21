# Autentificare prin Active Directory

Provider **paralel** cu conturile locale, nu în locul lor. Cât timp
`LDAP_ENABLED=false`, niciun cod de aici nu se execută și aplicația se comportă
exact ca înainte.

## Cuprins

1. [Ce face și ce nu face](#ce-face-si-ce-nu-face)
2. [Cum se alege providerul la login](#cum-se-alege-providerul-la-login)
3. [Configurare](#configurare)
4. [Laborator: Samba AD în Docker](#laborator-samba-ad-in-docker)
5. [Certificatul și de ce contează](#certificatul-si-de-ce-conteaza)
6. [Roluri din grupuri AD](#roluri-din-grupuri-ad)
7. [Importul structurii organizatorice](#importul-structurii-organizatorice)
8. [Chei E2EE și schimbarea parolei în AD](#chei-e2ee-si-schimbarea-parolei-in-ad)
9. [Ce se întâmplă cu conturile locale existente](#ce-se-intampla-cu-conturile-locale-existente)
10. [Diagnosticare](#diagnosticare)

---

## Ce face si ce nu face

**Face:**

- verifică parola printr-un bind LDAPS la controlerul de domeniu;
- creează automat contul local la prima autentificare reușită (opțional);
- ia din AD numele, adresa de email, rolul (din grupuri), subdiviziunea (din
  atributul `department`) și starea contului (activ/dezactivat);
- importă structura de unități organizatorice, cu confirmarea administratorului;
- leagă conturi locale existente de conturi de domeniu.

**Nu face, deliberat:**

- nu scrie nimic în AD. Dreptul de scriere în directorul instituției nu se dă
  unei aplicații web, oricât de convenabil ar fi;
- nu copiază parola, nici măcar ca hash. `PasswordHash` rămâne gol pentru
  conturile de domeniu, deci un cont dezactivat în AD nu mai poate intra prin
  vreo copie locală rămasă valabilă;
- nu deduce cine conduce o subdiviziune. În AD nu există noțiunea asta, iar
  ghicirea ei din atributul `manager` ar acorda drepturi de distribuție a
  documentelor interne pe baza unei presupuneri. Șeful se stabilește în
  continuare din pagina „Structura organizatorică”;
- nu șterge subdiviziuni care au dispărut din AD. Ele pot avea documente
  distribuite și oameni încadrați; importul doar le semnalează;
- nu dezactivează 2FA. Al doilea factor rămâne al aplicației, indiferent de
  provider.

## Cum se alege providerul la login

| Situație | Ce se întâmplă |
|---|---|
| Contul există local, `AuthProvider = Local` | Verificare Argon2id, ca până acum. **Nu** se încearcă AD-ul, chiar dacă domeniul are un cont cu același nume |
| Contul există local, `AuthProvider = Ldap` | Bind LDAPS. Hash-ul local nu se consultă |
| Numele nu există local, `LDAP_AUTO_CREATE=true` | Bind LDAPS; la reușită, contul local se creează |
| Numele nu există local, `LDAP_AUTO_CREATE=false` | Refuz. Administratorul trebuie să lege contul întâi |

Numele se acceptă în toate cele trei forme - `ion.popescu`,
`SGDM\ion.popescu`, `ion.popescu@sgdm.local` - și duc la **același** cont local.
Fără normalizarea asta, aceeași persoană ar fi ajuns cu trei conturi, fiecare cu
propriile chei E2EE.

Ce rămâne valabil pentru conturile de domeniu:

- **blocarea după încercări eșuate** se aplică identic. Altfel, API-ul nostru ar
  deveni un instrument de forță brută împotriva AD-ului, iar contorul de blocare
  al domeniului s-ar declanșa pentru oameni care nu au greșit nimic;
- **DC-ul indisponibil** răspunde `503`, nu „parolă greșită”, și **nu** crește
  contorul de eșecuri: o cădere de rețea de câteva minute nu are voie să blocheze
  toate conturile instituției;
- **2FA, sesiunile per dispozitiv, auditul** funcționează la fel.

## Configurare

Totul trece prin `.env` (vezi `.env.example`, secțiunea „Active Directory”).
Minimul funcțional:

```
LDAP_ENABLED=true
LDAP_HOST=dc.sgdm.local
LDAP_BASE_DN=DC=sgdm,DC=local
LDAP_REALM=sgdm.local
LDAP_BIND_DN=CN=svc-sgdm,CN=Users,DC=sgdm,DC=local
MAI_LDAP_BIND_PASSWORD=...
LDAP_CA_FILE=/app/certs/ad-ca.pem
```

`LDAP_HOST` trebuie să fie **numele DNS**, nu adresa IP: certificatul e emis pe
nume, iar validarea TLS compară exact acel șir.

Contul de serviciu are nevoie doar de drept de citire. Lăsat gol, aplicația face
bind direct cu UPN-ul utilizatorului (`nume@realm`) și caută cu aceeași
conexiune - merge, dar atunci `LDAP_REALM` devine obligatoriu.

Aplicația **refuză să pornească** dacă:

- `LDAP_ENABLED=true` fără `LDAP_HOST` sau `LDAP_BASE_DN`;
- nici LDAPS, nici StartTLS nu sunt active (parolele ar circula în clar);
- amândouă sunt active simultan;
- maparea grupurilor pe roluri e scrisă greșit;
- în afara mediului Development: `LDAP_ALLOW_PLAINTEXT=true` sau
  `LDAP_ALLOW_UNTRUSTED_CERT=true`.

## Laborator: Samba AD in Docker

Serviciul `samba-ad` din `docker-compose.yml` e legat de profilul `ldap`, deci nu
pornește la un `docker compose up` obișnuit.

```powershell
docker compose --profile ldap up -d samba-ad
```

Prima pornire provizionează domeniul (durează un minut sau două). Urmărește
progresul:

```powershell
docker logs -f sgdm-samba-ad
```

Apoi creează OU-urile, grupurile, conturile de test și scoate certificatul:

```powershell
.\scripts\samba-ad-setup.ps1
```

Scriptul afișează la final amprenta SHA-256 a certificatului. Pune în `.env`
**una** dintre variante (`LDAP_CA_FILE` sau `LDAP_CERT_THUMBPRINT`), pune
`LDAP_ENABLED=true` și repornește API-ul:

```powershell
docker compose up -d --build api
```

Conturile create de script: `admin.sgdm` (grupul `SGDM-Admins`), `maria.rusu`
(`SGDM-Sefi`), `ion.popescu` (`SGDM-Utilizatori`), toate cu parola dată
scriptului (implicit `Parola-Test1`).

Containerul rulează privilegiat, pentru că Samba AD își gestionează propriul DNS
și cheile Kerberos. Exact de aceea nu are ce căuta în producție: acolo
controlerul de domeniu e un server separat, administrat de altcineva.

## Certificatul si de ce conteaza

La un bind simplu, parola merge la server în clar, în interiorul tunelului TLS.
Dacă tunelul se stabilește cu **orice** certificat, oricine reușește să se
interpună pe rețea (DNS otrăvit, ARP spoofing) primește parolele de domeniu ale
tuturor celor care se autentifică - inclusiv ale administratorilor. Validarea
certificatului este singurul lucru care împiedică asta.

Trei moduri, în ordinea strictaței:

1. **Amprentă fixată** (`LDAP_CERT_THUMBPRINT`) - cea mai strictă și cea
   recomandată pentru un DC cu certificat autosemnat. Se schimbă doar când se
   reemite certificatul.
2. **CA propriu** (`LDAP_CA_FILE`) - certificatul serverului trebuie să ducă
   exact la CA-ul dat, nu la unul din magazinul sistemului.
3. **Magazinul de încredere al sistemului** - implicit, potrivit când DC-ul are
   un certificat emis de o autoritate recunoscută.

`LDAP_ALLOW_UNTRUSTED_CERT=true` acceptă orice și scrie un avertisment în log la
fiecare conexiune. Există pentru prima jumătate de oră de laborator; în afara
mediului Development, API-ul nu pornește cu el.

Butonul **Testează** din pagina „Active Directory” afișează subiectul și
amprenta certificatului prezentat de server, cu un buton de copiere - nu trebuie
căutat pe server.

## Roluri din grupuri AD

```
LDAP_GROUP_ROLE_MAPPINGS=SGDM-Admins=Administrator;SGDM-Sefi=SefDirectie
LDAP_DEFAULT_ROLE=Utilizator
```

Se acceptă DN-ul complet (`CN=SGDM-Admins,OU=Grupuri,DC=sgdm,DC=local`) sau doar
numele grupului. Când un cont e în mai multe grupuri mapate, câștigă rolul cel
mai mare - alternativa (primul din listă) ar face ordinea liniilor din
configurare să conteze în tăcere.

Se citesc și apartenențele **indirecte** (grup în grup), prin regula de potrivire
în lanț a AD-ului. Atributul `memberOf` conține doar apartenențele directe, iar
într-o instituție „SGDM-Admins” e aproape sigur un grup care conține alte
grupuri: fără pasul acesta, maparea ar părea configurată corect și n-ar acorda
nimănui rolul.

Pentru conturile de domeniu, aplicația **refuză** schimbarea rolului din pagina
de utilizatori: modificarea ar fi ștearsă la următorul login, iar administratorul
ar rămâne convins că a retras un drept pe care contul îl are în continuare.
Rolul se schimbă în AD, mutând contul între grupuri.

## Importul structurii organizatorice

Pagina **Administrare → Active Directory** are trei pași: stare, test de
conexiune, import.

Importul afișează întâi un **plan**: ce s-ar crea, ce s-ar actualiza, ce rămâne
neschimbat. Nimic nu se scrie până la confirmare, iar administratorul poate
debifa rânduri.

- adâncimea din AD devine nivelul subdiviziunii (primul nivel configurat pentru
  OU-urile de vârf, al doilea pentru copiii lor și așa mai departe). Sub ultimul
  nivel configurat se creează automat niveluri noi, ca un copil să rămână mereu
  pe un rang strict mai mare decât părintele;
- o subdiviziune existentă cu **aceeași denumire** se leagă de OU-ul din AD în
  loc să fie dublată. Altfel, utilizatorii ar rămâne încadrați în copia veche și
  n-ar mai apărea în distribuția documentelor;
- la a doua rulare, un OU deja importat se recunoaște după DN și nu se mai
  schimbă nimic;
- OU-urile dispărute din AD sunt doar semnalate, niciodată șterse.

Încadrarea utilizatorilor **nu** se modifică la import. Ea vine, separat, din
atributul `department` al fiecărui cont, comparat cu denumirea sau prescurtarea
subdiviziunilor. Fără o potrivire exactă și unică, contul rămâne neîncadrat: o
subdiviziune ghicită greșit ar trimite documentele interne altor oameni decât
trebuie.

## Chei E2EE si schimbarea parolei in AD

Cheile private ale utilizatorului sunt încuiate, în browser, cu o cheie derivată
din parola contului. Pentru un cont de domeniu, aceea e parola din AD - iar AD-ul
nu ne anunță când se schimbă.

Cum se rezolvă:

1. la fiecare autentificare citim `pwdLastSet` din AD și îl comparăm cu momentul
   ultimei împachetări a cheilor (`KeysWrappedAt`);
2. dacă parola s-a schimbat după, răspunsul de login conține
   `keyRewrapRequired`, iar ecranul de deblocare cere **parola veche** și
   **parola actuală**;
3. browserul descuie blobul cu cea veche, îl reîncuie cu cea nouă și trimite
   rezultatul la `PATCH /api/Keys/rewrap`. Cheile publice nu se schimbă, deci
   fișierele primite până atunci rămân accesibile.

Fără pasul acesta, utilizatorul ar vedea doar „parolă greșită” la descuiere,
imediat după un login reușit.

Ecranul are și intrare manuală („Parola a fost schimbată în afara aplicației?”),
pentru cazurile în care momentul schimbării nu e cunoscut - de exemplu la un cont
local tocmai legat de domeniu.

Dovada parolei cerută de operațiile cu chei (`POST /api/Keys`,
`PATCH /api/Keys/rewrap`, `POST /api/Keys/verify-password`) se face, pentru
conturile de domeniu, printr-un bind LDAPS. Dacă DC-ul nu răspunde, răspunsul
este `503`, nu „parolă greșită”: utilizatorul trebuie să afle că problema e la
serviciul de domeniu, nu la tastatura lui.

## Ce se intampla cu conturile locale existente

Nimic, până când administratorul le leagă explicit. Un cont local **nu** devine
cont de domeniu fiindcă domeniul conține un cont cu același nume - ar fi o
preluare de identitate la îndemâna oricui poate crea un cont în AD.

Legarea se face din pagina de utilizatori (butonul cu lanț) sau prin
`POST /api/Directory/users/{id}/link`. La legare:

- hash-ul local se șterge. Cât timp rămâne, există o a doua parolă validă pentru
  același om, necunoscută administratorului de domeniu și nesupusă politicii lui;
- dacă utilizatorul avea chei E2EE, ele rămân încuiate cu parola locală veche:
  la prima autentificare i se cere o dată parola veche, pentru reîmpachetare.

Deconectarea (`unlink`) lasă contul **fără nicio parolă utilizabilă** până când
administratorul îi stabilește una. Este intenționat: altfel, desfacerea legăturii
ar redeschide contul cu o parolă veche, rămasă din trecut.

Pentru conturile de domeniu, aplicația refuză - cu mesaj explicit - schimbarea
parolei din profil, resetarea administrativă, linkul de resetare pe email și
retrimiterea invitației. Toate ar scrie un hash pe care autentificarea nu îl
consultă niciodată.

## Diagnosticare

| Simptom | Cauză probabilă |
|---|---|
| „Serviciul de domeniu nu răspunde” (503) | Numele din `LDAP_HOST` nu se rezolvă din container, portul e închis sau certificatul e respins. Logul API-ului spune care |
| Certificat respins | Amprenta din `LDAP_CERT_THUMBPRINT` nu corespunde, CA-ul din `LDAP_CA_FILE` nu se potrivește, sau `LDAP_HOST` e o adresă IP în loc de nume |
| Utilizatorul intră, dar cu rol greșit | Grupul nu e în `LDAP_GROUP_ROLE_MAPPINGS`, sau apartenența e indirectă și serverul nu suportă regula de potrivire în lanț (apare un avertisment în log) |
| Contul rămâne neîncadrat | `department` din AD nu corespunde exact denumirii sau prescurtării vreunei subdiviziuni active |
| „Există deja un cont local cu acest nume” | Cont local nelegat. Legați-l explicit din pagina de utilizatori |
| Cheile nu se descuie după un login reușit | Parola s-a schimbat în AD. Folosiți „Parola a fost schimbată în afara aplicației?” din ecranul de deblocare |

Verificări utile din linia de comandă (un singur rând fiecare, PowerShell):

```powershell
docker exec sgdm-api bash -lc "getent hosts dc.sgdm.local"
```

```powershell
docker exec sgdm-samba-ad bash -lc "samba-tool user list"
```

```powershell
docker exec sgdm-samba-ad bash -lc "ldapsearch -H ldaps://dc.sgdm.local -D 'CN=svc-sgdm,CN=Users,DC=sgdm,DC=local' -w 'Parola-Test1' -b 'DC=sgdm,DC=local' '(sAMAccountName=ion.popescu)' memberOf department pwdLastSet"
```

Auditul consemnează separat `DirectoryAccountProvisioned` (cont creat automat din
AD), `DirectoryAccountSynchronized` (atribute aduse din AD) și
`DirectoryStructureImported` (import de structură confirmat), ca un control
intern să poată distinge ce a decis un om de ce a decis domeniul.
