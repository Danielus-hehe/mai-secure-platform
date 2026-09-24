-- ═══════════════════════════════════════════════════════════════════════════
-- 900 - Date demonstrative
--
-- DE RULAT DOAR ÎN DEZVOLTARE ȘI LA PREZENTARE. Nu în producție.
--
-- Se lipește direct în SQL Editor (DataGrip, DBeaver, psql) și se rulează.
-- Nu are nevoie de niciun tool extern.
--
-- ── Cum sunt rezolvate parolele ────────────────────────────────────────────
--
-- Hashurile Argon2id NU pot fi scrise direct în SQL: Argon2:Pepper (parametrul
-- „secret" al algoritmului) intră în calcul, deci un hash generat în altă parte
-- nu s-ar verifica pe instalarea ta.
--
-- Soluția: toți utilizatorii demo primesc hashul COPIAT de la un cont real care
-- deja există. Adică se autentifică toți cu parola contului sursă, oricare ar fi
-- ea și oricare ar fi pepperul. Fără tooling, fără presupuneri.
--
-- Setează mai jos numele contului sursă. Trebuie să existe și să aibă o parolă
-- funcțională.
--
-- ── Limitare de menționat la prezentare ────────────────────────────────────
--
-- Utilizatorii demo NU au chei criptografice: cheile se generează în browser, la
-- prima autentificare, iar serverul nu le poate fabrica - exact asta e garanția
-- E2EE. Prin urmare transferurile din seed au IsEncrypted = false și sunt puse
-- în stări terminale (descărcat, expirat, retras). Nimeni nu va încerca să le
-- deschidă, iar listele, filtrele, dovada de primire și jurnalul de audit sunt
-- pline.
--
-- Pentru un transfer criptat real în demonstrație: autentifică-te cu două
-- conturi din seed, lasă-le să-și genereze cheile, și trimite un fișier live.
-- Merită făcut înainte de prezentare, nu în timpul ei.
--
-- ── Fișiere în MinIO ──────────────────────────────────────────────────────
--
-- Documentele normative referă chei de forma documents/demo/doc-{n}/v{m}.txt,
-- cele interne internal/2026/09/intdoc-{n}.enc. Fișierele NU se creează aici:
-- după acest script, rulați (din rădăcina repo-ului)
--     dotnet run --project MAI.Api -- demo:seed-files
-- Comanda scrie fișierele prin depozitul criptat și înlocuiește amprentele
-- SHA-256 de mai jos (provizorii) cu cele ale conținutului real. Fără ea,
-- documentele apar în listă, dar descărcarea dă 404.
--
-- ── Rulare repetată ────────────────────────────────────────────────────────
--
-- Idempotent. Șterge întâi datele demo anterioare (recunoscute după sufixul
-- '.demo' din username), apoi le recreează. Conturile tale reale NU sunt atinse.
-- ═══════════════════════════════════════════════════════════════════════════

DO $$
DECLARE
    -- ⬇⬇⬇  SINGURUL LUCRU DE MODIFICAT  ⬇⬇⬇
v_sursa_parola  text := 'admin';   -- username-ul unui cont real, funcțional
    -- ⬆⬆⬆

    v_hash          text;
    v_now           timestamptz := now();

    v_admin         uuid;
    v_sef1          uuid;
    v_sef2          uuid;
    v_sef3          uuid;
    v_user_ids      uuid[];
    v_id            uuid;
    v_sender        uuid;
    v_recipient     uuid;
    v_recipient2    uuid;
    v_tid           uuid;
    v_unit_ids      uuid[];
    v_sub_unit_ids  uuid[];
    v_created       timestamptz;
    v_status        int;
    v_i             int;
    v_j             int;
    v_doc_id        uuid;
    v_intdoc_id     uuid;

    v_prenume       text[] := ARRAY['Ion','Maria','Andrei','Elena','Vasile','Cristina',
                                    'Mihai','Ana','Sergiu','Natalia','Dumitru','Irina',
                                    'Alexandru','Valentina','Nicolae','Ecaterina',
                                    'Tudor','Alina','Gheorghe','Diana'];
    v_nume          text[] := ARRAY['Popescu','Rusu','Ciobanu','Lungu','Moraru','Cebotari',
                                    'Bejan','Grosu','Munteanu','Sirbu','Balan','Ursu',
                                    'Cojocaru','Rotaru','Botnari','Pascaru',
                                    'Bivol','Ceban','Gutu','Popa'];
    v_directii      text[] := ARRAY['Directia Generala Politie','Directia Tehnologii Informationale',
                                    'Directia Juridica','Inspectoratul General pentru Situatii de Urgenta',
                                    'Directia Resurse Umane'];
    v_sectii        text[] := ARRAY['Sectia Investigatii','Sectia Analiza','Sectia Infrastructura',
                                    'Sectia Operativa','Sectia Evidenta'];
    v_servicii      text[] := ARRAY['Serviciul Analiza','Serviciul Suport Tehnic',
                                    'Serviciul Monitorizare','Serviciul Documente'];
    v_fisiere       text[] := ARRAY['Raport_trimestrial_Q3.pdf','Ordin_intern_142.docx',
                                    'Nota_informativa_securitate.pdf','Statistici_interventii.xlsx',
                                    'Proces_verbal_sedinta.docx','Plan_actiuni_2026.pdf',
                                    'Lista_echipamente.xlsx','Instructiune_operationala.pdf',
                                    'Raport_incident_089.docx','Buget_estimativ.xlsx',
                                    'Analiza_riscuri_T3.pdf','Schema_retea_actualizata.vsdx',
                                    'Protocol_cooperare.docx','Raport_audit_intern.pdf',
                                    'Fisa_post_actualizata.docx'];
    v_ip            text[] := ARRAY['10.20.4.17','10.20.4.31','10.20.7.102','10.20.11.5',
                                    '10.20.4.88','10.20.3.42','10.20.8.15','10.20.6.201'];
BEGIN

    -- ── Contul sursă pentru parolă ─────────────────────────────────────────
SELECT "PasswordHash" INTO v_hash FROM "Users" WHERE "Username" = v_sursa_parola;

IF v_hash IS NULL THEN
        RAISE EXCEPTION
            'Contul sursa "%" nu exista. Modifica v_sursa_parola cu username-ul unui cont real.',
            v_sursa_parola;
END IF;

    -- ── Curățenie: doar datele demo anterioare ─────────────────────────────
DELETE FROM "AuditLogs"
WHERE "Username" LIKE '%.demo';

DELETE FROM "InternalDocumentRecipients"
WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

DELETE FROM "InternalDocumentTargets"
WHERE "DocumentId" IN (
    SELECT "Id" FROM "InternalDocuments"
    WHERE "AuthorId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo'));

DELETE FROM "InternalDocuments"
WHERE "AuthorId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

DELETE FROM "TransferRecipients"
WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

DELETE FROM "FileTransfers"
WHERE "SenderId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

DELETE FROM "DocumentVersions"
WHERE "DocumentId" IN (
    SELECT "Id" FROM "Documents"
    WHERE "CreatedById" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo'));

DELETE FROM "Documents"
WHERE "CreatedById" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

DELETE FROM "UserSessions"
WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

UPDATE "OrgUnits" SET "HeadUserId" = NULL WHERE "Code" LIKE 'DEMO-%';

DELETE FROM "Users" WHERE "Username" LIKE '%.demo';

DELETE FROM "OrgUnits" WHERE "Code" LIKE 'DEMO-%' AND "Type" >= 300;
DELETE FROM "OrgUnits" WHERE "Code" LIKE 'DEMO-%' AND "Type" >= 200;
DELETE FROM "OrgUnits" WHERE "Code" LIKE 'DEMO-%';

-- ══════════════════════════════════════════════════════════════════════
-- 0. STRUCTURA ORGANIZATORICĂ
-- ══════════════════════════════════════════════════════════════════════

v_unit_ids := ARRAY[]::uuid[];
    v_sub_unit_ids := ARRAY[]::uuid[];

    -- 5 direcții (nivel 100)
FOR v_i IN 1..5 LOOP
        v_id := gen_random_uuid();
INSERT INTO "OrgUnits" ("Id", "Name", "Code", "Type", "ParentId", "IsActive", "CreatedAt")
VALUES (v_id, v_directii[v_i] || ' (demo)', 'DEMO-' || v_i, 100, NULL, TRUE, v_now);
v_unit_ids := array_append(v_unit_ids, v_id);
END LOOP;

    -- Secții sub primele 3 direcții (nivel 200)
FOR v_i IN 1..5 LOOP
        v_id := gen_random_uuid();
INSERT INTO "OrgUnits" ("Id", "Name", "Code", "Type", "ParentId", "IsActive", "CreatedAt")
VALUES (v_id, v_sectii[v_i] || ' (demo)', 'DEMO-S' || v_i, 200,
        v_unit_ids[1 + ((v_i - 1) % 3)], TRUE, v_now);
v_sub_unit_ids := array_append(v_sub_unit_ids, v_id);
END LOOP;

    -- Servicii sub primele 2 secții (nivel 300)
FOR v_i IN 1..4 LOOP
        v_id := gen_random_uuid();
INSERT INTO "OrgUnits" ("Id", "Name", "Code", "Type", "ParentId", "IsActive", "CreatedAt")
VALUES (v_id, v_servicii[v_i] || ' (demo)', 'DEMO-V' || v_i, 300,
        v_sub_unit_ids[1 + ((v_i - 1) % 2)], TRUE, v_now);
END LOOP;

    -- ══════════════════════════════════════════════════════════════════════
    -- 1. UTILIZATORI: 20 conturi
    -- ══════════════════════════════════════════════════════════════════════
    -- 1 administrator, 3 șefi de direcție, 16 utilizatori.

    v_user_ids := ARRAY[]::uuid[];

FOR v_i IN 1..20 LOOP
        v_id := gen_random_uuid();

INSERT INTO "Users" (
    "Id", "Username", "Email", "PasswordHash", "FullName", "OrgUnitId",
    "Role", "IsActive", "CreatedAt",
    "FailedLoginAttempts", "TwoFactorEnabled", "TwoFactorChallengeAttempts",
    "MustChangePassword", "EmailConfirmed",
    "LastLoginAt"
) VALUES (
             v_id,
             lower(v_prenume[v_i]) || '.' || lower(v_nume[v_i]) || '.demo',
             lower(v_prenume[v_i]) || '.' || lower(v_nume[v_i]) || '@mai.demo',
             v_hash,
             v_prenume[v_i] || ' ' || v_nume[v_i],
             -- Distribuiți pe unitățile organizatorice
             CASE
                 WHEN v_i <= 5 THEN v_unit_ids[v_i]
                 WHEN v_i <= 10 THEN v_sub_unit_ids[v_i - 5]
                 ELSE v_unit_ids[1 + ((v_i - 1) % 5)]
END,
            CASE
                WHEN v_i = 1 THEN 3              -- Administrator
                WHEN v_i IN (2,3,4) THEN 2       -- Șef direcție
                ELSE 1                            -- Utilizator
END,
            v_i NOT IN (19, 20),                  -- 2 dezactivați
            v_now - (interval '1 day' * (300 - v_i * 10)),
            0,
            v_i IN (2, 3, 5, 7, 9, 12),          -- 2FA activat
            0,
            FALSE,                                -- MustChangePassword = false (demo)
            TRUE,                                 -- EmailConfirmed = true
            v_now - (interval '1 hour' * v_i)
        );

        v_user_ids := array_append(v_user_ids, v_id);
END LOOP;

    v_admin := v_user_ids[1];
    v_sef1  := v_user_ids[2];
    v_sef2  := v_user_ids[3];
    v_sef3  := v_user_ids[4];

    -- Șefii conduc direcțiile lor
UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[2] WHERE "Id" = v_unit_ids[1];
UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[3] WHERE "Id" = v_unit_ids[2];
UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[4] WHERE "Id" = v_unit_ids[3];

-- Un utilizator obișnuit conduce o secție
UPDATE "Users" SET "OrgUnitId" = v_sub_unit_ids[1] WHERE "Id" = v_user_ids[6];
UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[6] WHERE "Id" = v_sub_unit_ids[1];
UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[7] WHERE "Id" = v_sub_unit_ids[2];

-- Un cont blocat (pentru alerte)
UPDATE "Users"
SET "FailedLoginAttempts" = 7,
    "LockoutEndsAt"       = v_now + interval '20 minutes',
    "LastFailedLoginAt"   = v_now - interval '2 minutes'
WHERE "Id" = v_user_ids[10];

-- ══════════════════════════════════════════════════════════════════════
-- 2. SESIUNI ACTIVE: demonstrează pagina de sesiuni
-- ══════════════════════════════════════════════════════════════════════

FOR v_i IN 1..12 LOOP
        -- AbsoluteExpiresAt (limita fixa a sesiunii, migrarea
        -- ConcurrencyAndSessionLifetime) e pusa la 12 ore de ACUM, nu de la
        -- CreatedAt: sesiunile demo sunt deschise "de cateva zile" ca lista sa
        -- arate realist, dar trebuie sa ramana active pentru demonstratie.
        INSERT INTO "UserSessions" (
            "Id", "UserId", "RefreshTokenHash", "CreatedAt", "LastSeenAt",
            "ExpiresAt", "AbsoluteExpiresAt", "RevokedAt", "RevokedReason", "UserAgent", "IpAddress"
        ) VALUES (
            gen_random_uuid(),
            v_user_ids[v_i],
            encode(sha256(('session-demo-' || v_i)::bytea), 'hex'),
            v_now - (interval '1 day' * (v_i % 5)),
            v_now - (interval '1 hour' * v_i),
            v_now + interval '12 hours',
            v_now + interval '12 hours',
            CASE WHEN v_i IN (10, 11, 12) THEN v_now - interval '3 hours' END,
            CASE WHEN v_i IN (10, 11, 12) THEN 'Revocare de la distanta (demo)' END,
            CASE v_i % 4
                WHEN 0 THEN 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/128.0'
                WHEN 1 THEN 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15) Safari/605.1'
                WHEN 2 THEN 'Mozilla/5.0 (X11; Linux x86_64) Firefox/130.0'
                ELSE 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0) Mobile/15E148'
            END,
            v_ip[1 + (v_i % 8)]
        );
END LOOP;

    -- ══════════════════════════════════════════════════════════════════════
    -- 3. TRANSFERURI: 50 transferuri cu dovezi de primire
    -- ══════════════════════════════════════════════════════════════════════

FOR v_i IN 1..50 LOOP
        v_sender    := v_user_ids[1 + (v_i % 18)];
        v_recipient := v_user_ids[1 + ((v_i + 7) % 18)];

        IF v_sender = v_recipient THEN
            CONTINUE;
END IF;

        v_created := v_now - (interval '1 hour' * (v_i * 4 + (v_i % 7)));

        -- Distribuție: ~20 Pending, ~15 Downloaded, ~8 Expired, ~7 Revoked
        v_status := CASE
            WHEN v_i % 10 = 0 THEN 3   -- Revoked
            WHEN v_i % 7  = 0 THEN 2   -- Expired
            WHEN v_i % 3  = 0 THEN 0   -- Pending
            ELSE 1                      -- Downloaded
END;

        v_tid := gen_random_uuid();

INSERT INTO "FileTransfers" (
    "Id", "SenderId", "FileName", "StorageKey",
    "FileSize", "CiphertextSize", "ChecksumSHA256",
    "Status", "CreatedAt", "ExpiresAt",
    "IsEncrypted", "RevokedAt", "RevokedReason", "Category", "AllowForward"
) VALUES (
             v_tid,
             v_sender,
             v_fisiere[1 + (v_i % 15)],
             CASE WHEN v_status = 0
                      THEN 'transfers/demo/pending-' || v_i || '.enc'
                  ELSE '' END,
             (30000 + v_i * 97000)::bigint,
             (30000 + v_i * 97000 + 4096)::bigint,
             encode(sha256(('demo-transfer-' || v_i)::bytea), 'hex'),
             v_status,
             v_created,
             v_created + interval '7 days',
             false,
             CASE WHEN v_status = 3 THEN v_created + interval '35 minutes' END,
             CASE WHEN v_status = 3 THEN
                      (ARRAY['Versiune gresita a documentului',
                       'Trimis destinatarului gresit',
                       'Document actualizat, se retrimite',
                       'Anulat la cererea sefului'])[1 + (v_i % 4)]
                 END,
             v_i % 4,
             v_i % 3 = 0
         );

-- Destinatarul direct
INSERT INTO "TransferRecipients" (
    "TransferId", "UserId", "EncryptedKeyForUser", "ForwardedById",
    "SentAt", "DownloadedAt", "SignatureValid"
) VALUES (
             v_tid, v_recipient, '', NULL, v_created,
             CASE WHEN v_status IN (0, 1) AND (v_status = 1 OR v_i % 5 = 0)
                      THEN v_created + interval '2 hours' + (interval '1 minute' * v_i) END,
             CASE WHEN v_status IN (0, 1) AND (v_status = 1 OR v_i % 5 = 0)
                      THEN (v_i <> 8 AND v_i <> 33) END
         );

-- Al doilea destinatar la fiecare al 4-lea transfer
v_recipient2 := v_user_ids[1 + ((v_i + 11) % 18)];
        IF v_i % 4 = 0 AND v_recipient2 NOT IN (v_sender, v_recipient) THEN
            INSERT INTO "TransferRecipients" (
                "TransferId", "UserId", "EncryptedKeyForUser", "ForwardedById",
                "SentAt", "DownloadedAt", "SignatureValid"
            ) VALUES (
                v_tid, v_recipient2, '', NULL, v_created,
                CASE WHEN v_status = 1 THEN v_created + interval '6 hours' END,
                CASE WHEN v_status = 1 THEN true END
            );
END IF;

        -- Forward-uri la fiecare al 8-lea transfer
        IF v_i % 8 = 0 AND v_status IN (0, 1) THEN
            v_recipient2 := v_user_ids[1 + ((v_i + 14) % 18)];
            IF v_recipient2 NOT IN (v_sender, v_recipient) THEN
                INSERT INTO "TransferRecipients" (
                    "TransferId", "UserId", "EncryptedKeyForUser", "ForwardedById",
                    "SentAt", "DownloadedAt", "SignatureValid"
                ) VALUES (
                    v_tid, v_recipient2, '', v_recipient,
                    v_created + interval '4 hours',
                    CASE WHEN v_status = 1 THEN v_created + interval '8 hours' END,
                    CASE WHEN v_status = 1 THEN true END
                );
END IF;
END IF;
END LOOP;

    -- ══════════════════════════════════════════════════════════════════════
    -- 4. DOCUMENTE NORMATIVE cu versiuni: 12 documente, ~25 versiuni
    -- ══════════════════════════════════════════════════════════════════════
    -- Cheile de stocare referă documents/demo/doc-{i}/v{j}.txt. Fișierele și
    -- amprentele reale le scrie comanda demo:seed-files (vezi antetul).

FOR v_i IN 1..12 LOOP
        v_doc_id := gen_random_uuid();

INSERT INTO "Documents" (
    "Id", "Title", "DocumentNumber", "Category", "Keywords",
    "CurrentVersion", "CreatedById", "CreatedAt"
) VALUES (
             v_doc_id,
             (ARRAY['Regulament intern de ordine','Procedura de acces in sediu',
              'Instructiune privind protectia datelor','Plan de continuitate operationala',
              'Norme de securitate informationala','Ghid de raportare a incidentelor',
              'Politica de retentie a documentelor','Procedura de arhivare',
              'Regulament privind transferul de fisiere','Instructiune de utilizare SGDM',
              'Plan anual de instruire','Norme de clasificare a informatiei'])[v_i],
             'MAI-' || to_char(v_now, 'YYYY') || '-' || lpad(v_i::text, 4, '0'),
             (ARRAY['Regulament','Procedura','Instructiune','Plan','Norma'])[1 + (v_i % 5)],
             'securitate, intern, mai, documentatie, proceduri',
             1 + (v_i % 3),
             v_user_ids[1 + (v_i % 18)],
             v_now - (interval '1 day' * v_i * 8)
         );

FOR v_j IN 1..(1 + (v_i % 3)) LOOP
            INSERT INTO "DocumentVersions" (
                "Id", "DocumentId", "VersionNumber", "EncryptedStoragePath",
                "ChecksumSHA256", "CreatedBy", "CreatedAt", "ChangeNotes"
            ) VALUES (
                gen_random_uuid(),
                v_doc_id,
                v_j,
                'documents/demo/doc-' || v_i || '/v' || v_j || '.txt',
                encode(sha256(('doc-demo-' || v_i || '-v' || v_j)::bytea), 'hex'),
                (SELECT "Username" FROM "Users" WHERE "Id" = v_user_ids[1 + (v_i % 18)]),
                v_now - (interval '1 day' * (v_i * 8 - v_j * 2)),
                CASE v_j
                    WHEN 1 THEN 'Versiune initiala'
                    WHEN 2 THEN 'Corectii dupa avizare juridica'
                    ELSE 'Actualizare conform noilor norme'
                END
            );
END LOOP;
END LOOP;

    -- ══════════════════════════════════════════════════════════════════════
    -- 5. DOCUMENTE INTERNE cu distribuție pe structura organizatorică
    -- ══════════════════════════════════════════════════════════════════════

FOR v_i IN 1..8 LOOP
        v_intdoc_id := gen_random_uuid();

INSERT INTO "InternalDocuments" (
    "Id", "Title", "Number", "Summary", "AuthorId", "AuthorOrgUnitId",
    "Status", "DistributionMode", "IncludeSubunits",
    "RequiresAcknowledgement", "FileName", "ContentType", "FileSize",
    "Sha256", "StorageKey", "CreatedAt", "PublishedAt"
) VALUES (
             v_intdoc_id,
             (ARRAY['Dispozitie privind verificarea accesului',
              'Nota informativa nr. 14',
              'Circulara privind programul de lucru',
              'Dispozitie nr. D-2026/0015',
              'Nota privind instruirea periodica',
              'Circulara - actualizare SGDM',
              'Dispozitie privind backup-ul datelor',
              'Nota privind auditarea accesului'])[v_i],
             'D-2026/' || lpad(v_i::text, 4, '0'),
             (ARRAY['Se dispune verificarea accesului in toate subdiviziunile.',
              'Se aduce la cunostinta noua procedura de raportare.',
              'Programul de lucru se modifica incepand cu 01.10.2026.',
              'Se numeste comisia de inventariere IT.',
              'Angajatii sunt obligati sa parcurga modulul de securitate.',
              'Platforma SGDM a fost actualizata cu noi functionalitati.',
              'Se aproba procedura de backup si restaurare.',
              'Se va efectua un audit al accesului la sistemele informatice.'])[v_i],
             v_user_ids[CASE WHEN v_i <= 4 THEN 2 ELSE 3 END],
             v_unit_ids[CASE WHEN v_i <= 4 THEN 1 ELSE 2 END],
             CASE WHEN v_i <= 7 THEN 1 ELSE 0 END,  -- Published sau Draft
             CASE v_i % 3
                 WHEN 0 THEN 0   -- MyUnitTree
                WHEN 1 THEN 4   -- SpecificUsers
                ELSE 5          -- WholeInstitution
            END,
             TRUE,
             v_i <= 6,   -- 6 cer confirmare, 2 nu
             'dispozitie_' || v_i || '.txt',
             'text/plain',
             (800 + v_i * 120)::bigint,
             encode(sha256(('intdoc-demo-' || v_i)::bytea), 'hex'),
             'internal/2026/09/intdoc-' || v_i || '.enc',
             v_now - (interval '1 day' * (v_i * 3)),
             CASE WHEN v_i <= 7
                      THEN v_now - (interval '1 day' * (v_i * 3)) + interval '2 hours' END
         );

-- Distribuie la 5-8 destinatari
IF v_i <= 7 THEN   -- doar cele Published
            FOR v_j IN 1..LEAST(8, 18) LOOP
                -- evităm autorul
                IF v_user_ids[v_j] = v_user_ids[CASE WHEN v_i <= 4 THEN 2 ELSE 3 END] THEN
                    CONTINUE;
END IF;

                EXIT WHEN v_j > 5 + (v_i % 4);

INSERT INTO "InternalDocumentRecipients" (
    "DocumentId", "UserId", "OrgUnitId", "AddedAt",
    "FirstOpenedAt", "AcknowledgedAt"
) VALUES (
             v_intdoc_id,
             v_user_ids[v_j],
             (SELECT "OrgUnitId" FROM "Users" WHERE "Id" = v_user_ids[v_j]),
             v_now - (interval '1 day' * (v_i * 3)) + interval '2 hours',
             -- Majoritatea au deschis
             CASE WHEN v_j <= 4 OR v_j % 2 = 0
                         THEN v_now - (interval '1 day' * (v_i * 3)) + (interval '3 hours' * v_j) END,
             -- O parte au confirmat
             CASE WHEN v_j <= 3 AND v_i <= 6
                      THEN v_now - (interval '1 day' * (v_i * 3)) + (interval '5 hours' * v_j) END
         );
END LOOP;
END IF;
END LOOP;

    -- ══════════════════════════════════════════════════════════════════════
    -- 6. JURNAL DE AUDIT: ~500 rânduri, toate acțiunile și rezultatele
    -- ══════════════════════════════════════════════════════════════════════

FOR v_i IN 1..500 LOOP
        v_id := v_user_ids[1 + (v_i % 18)];

INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                         "Result", "IpAddress", "Timestamp")
SELECT
    gen_random_uuid(),
    v_id,
    u."Username",
    CASE
        WHEN v_i % 29 = 0 THEN 20   -- InternalDocumentRepealed
        WHEN v_i % 23 = 0 THEN 19   -- InternalDocumentAcknowledged
        WHEN v_i % 19 = 0 THEN 18   -- InternalDocumentOpened
        WHEN v_i % 17 = 0 THEN 10   -- TransferRevoked
        WHEN v_i % 13 = 0 THEN 11   -- SessionRevoked
        WHEN v_i % 11 = 0 THEN 7    -- UserUpdated
        WHEN v_i % 7  = 0 THEN 4    -- DocumentCreate
        WHEN v_i % 5  = 0 THEN 3    -- FileDownload
        WHEN v_i % 3  = 0 THEN 2    -- FileUpload
        WHEN v_i % 2  = 0 THEN 1    -- Logout
        ELSE 0                       -- Login
        END,
    CASE
        WHEN v_i % 29 = 0 THEN 'Document intern abrogat ''Circulara privind programul de lucru'''
        WHEN v_i % 23 = 0 THEN 'Document intern confirmat ''Dispozitie privind verificarea accesului'''
        WHEN v_i % 19 = 0 THEN 'Document intern deschis ''Nota informativa nr. 14'''
        WHEN v_i % 17 = 0 THEN 'Transfer retras ''' || v_fisiere[1 + (v_i % 15)] || ''' inainte de descarcare'
        WHEN v_i % 13 = 0 THEN 'Sesiune inchisa de la distanta (Chrome pe Windows)'
        WHEN v_i % 11 = 0 THEN 'Date de profil actualizate'
        WHEN v_i % 7  = 0 THEN 'Document creat MAI-2026-' || lpad((v_i % 12 + 1)::text, 4, '0')
        WHEN v_i % 5  = 0 THEN 'Fisier descarcat si decriptat ''' || v_fisiere[1 + (v_i % 15)] || ''', semnatura expeditorului VALIDA'
        WHEN v_i % 3  = 0 THEN 'Fisier incarcat ''' || v_fisiere[1 + (v_i % 15)] || ''''
        WHEN v_i % 2  = 0 THEN 'Delogare, sesiune inchisa'
        WHEN v_i % 37 = 0 THEN 'Parola incorecta (3/5)'
        ELSE 'Autentificare reusita'
        END,
    CASE
        WHEN v_i % 17 = 0 OR v_i % 13 = 0 THEN 2   -- Warning
                WHEN v_i % 19 = 0 AND v_i % 29 = 0 THEN 2  -- Warning
                WHEN v_i % 37 = 0                  THEN 1   -- Failure
                ELSE 0                                       -- Success
END,
            v_ip[1 + (v_i % 8)],
            v_now - (interval '13 minutes' * v_i)
          FROM "Users" u WHERE u."Id" = v_id;
END LOOP;

    -- Rânduri anume, pentru alertele de pe /admin ──────────────────────────

    -- Eșecuri consecutive pe un cont (alerta de forțare brută)
FOR v_i IN 1..8 LOOP
        INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                                 "Result", "IpAddress", "Timestamp")
SELECT gen_random_uuid(), u."Id", u."Username", 0,
       'Parola incorecta (' || v_i || '/5)', 1, '10.20.99.14',
       v_now - (interval '3 minutes' * v_i)
FROM "Users" u WHERE u."Id" = v_user_ids[10];
END LOOP;

    -- Autentificare cu cod de recuperare 2FA
INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                         "Result", "IpAddress", "Timestamp")
SELECT gen_random_uuid(), u."Id", u."Username", 0,
       'Autentificare cu COD DE RECUPERARE 2FA (7 ramase)', 2, '10.20.4.31',
       v_now - interval '2 days'
FROM "Users" u WHERE u."Id" = v_sef1;

-- Semnătură invalidă la descărcare
INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                         "Result", "IpAddress", "Timestamp")
SELECT gen_random_uuid(), u."Id", u."Username", 3,
       'Fisier descarcat ''Nota_informativa_securitate.pdf'', semnatura expeditorului INVALIDA',
       2, '10.20.7.102', v_now - interval '6 hours'
FROM "Users" u WHERE u."Id" = v_sef2;

-- StorageIntegrityFailure
INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                         "Result", "IpAddress", "Timestamp")
SELECT gen_random_uuid(), u."Id", u."Username", 21,
       'Descarcare refuzata ''Plan_actiuni_2026.pdf'': verificarea de integritate a esuat (eticheta GCM invalida)',
       1, '10.20.8.15', v_now - interval '14 hours'
FROM "Users" u WHERE u."Id" = v_user_ids[5];

-- Forward consemnat
INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                         "Result", "IpAddress", "Timestamp")
SELECT gen_random_uuid(), u."Id", u."Username", 12,
       'Transfer redirectionat ''Raport_trimestrial_Q3.pdf'' catre 2 destinatari noi',
       0, '10.20.4.17', v_now - interval '1 day'
FROM "Users" u WHERE u."Id" = v_user_ids[5];

-- Resetare parolă
INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                         "Result", "IpAddress", "Timestamp")
SELECT gen_random_uuid(), u."Id", u."Username", 13,
       'Link de resetare trimis pentru contul natalia.sirbu.demo', 2, '10.20.4.17',
       v_now - interval '3 days'
FROM "Users" u WHERE u."Id" = v_admin;

RAISE NOTICE 'Seed complet: 20 utilizatori, ~50 transferuri, 12 documente normative, 8 documente interne, 12 sesiuni, ~520 randuri de audit.';
    RAISE NOTICE 'Structura: 5 directii, 5 sectii, 4 servicii.';
    RAISE NOTICE 'Toti utilizatorii demo au parola contului "%".', v_sursa_parola;
    RAISE NOTICE 'Conturi: *.demo, administrator: %', (SELECT "Username" FROM "Users" WHERE "Id" = v_admin);

END $$;

-- ── Verificare ─────────────────────────────────────────────────────────────
-- SELECT "Username", "Role", "IsActive", "TwoFactorEnabled" FROM "Users" WHERE "Username" LIKE '%.demo' ORDER BY "Role" DESC, "Username";
-- SELECT "Status", count(*) FROM "FileTransfers" GROUP BY "Status" ORDER BY 1;
-- SELECT "Result", count(*) FROM "AuditLogs" GROUP BY "Result" ORDER BY 1;
-- SELECT "Action", count(*) FROM "AuditLogs" GROUP BY "Action" ORDER BY 2 DESC;
-- SELECT "Status", count(*) FROM "InternalDocuments" GROUP BY "Status";
-- SELECT count(*) FROM "InternalDocumentRecipients";
-- SELECT count(*) FROM "UserSessions";
