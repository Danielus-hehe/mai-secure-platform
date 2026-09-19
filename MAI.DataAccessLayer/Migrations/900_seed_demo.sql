-- ═══════════════════════════════════════════════════════════════════════════
-- 900 — Date demonstrative
--
-- DE RULAT DOAR ÎN DEZVOLTARE ȘI LA PREZENTARE. Nu în producție.
--
-- Se lipește direct în SQL Editor din Supabase și se rulează. Nu are nevoie de
-- niciun tool extern.
--
-- ── Cum sunt rezolvate parolele ────────────────────────────────────────────
--
-- Hashurile Argon2id NU pot fi scrise direct în SQL: Argon2:Pepper (parametrul
-- „secret” al algoritmului) intră în calcul, deci un hash generat în altă parte
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
-- prima autentificare, iar serverul nu le poate fabrica — exact asta e garanția
-- E2EE. Prin urmare transferurile din seed au IsEncrypted = false și sunt puse
-- în stări terminale (descărcat, expirat, retras). Nimeni nu va încerca să le
-- deschidă, iar listele, filtrele, dovada de primire și jurnalul de audit sunt
-- pline.
--
-- Pentru un transfer criptat real în demonstrație: autentifică-te cu două
-- conturi din seed, lasă-le să-și genereze cheile, și trimite un fișier live.
-- Merită făcut înainte de prezentare, nu în timpul ei.
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
    v_user_ids      uuid[];
    v_id            uuid;
    v_sender        uuid;
    v_recipient     uuid;
    v_recipient2    uuid;
    v_tid           uuid;
    v_unit_ids      uuid[];
    v_created       timestamptz;
    v_status        int;
    v_i             int;

    v_prenume       text[] := ARRAY['Ion','Maria','Andrei','Elena','Vasile','Cristina',
                                    'Mihai','Ana','Sergiu','Natalia','Dumitru','Irina'];
    v_nume          text[] := ARRAY['Popescu','Rusu','Ciobanu','Lungu','Moraru','Cebotari',
                                    'Bejan','Grosu','Munteanu','Sirbu','Balan','Ursu'];
    v_directii      text[] := ARRAY['Directia Generala Politie','Directia Tehnologii Informationale',
                                    'Directia Juridica','Inspectoratul General pentru Situatii de Urgenta',
                                    'Directia Resurse Umane'];
    v_fisiere       text[] := ARRAY['Raport_trimestrial_Q3.pdf','Ordin_intern_142.docx',
                                    'Nota_informativa_securitate.pdf','Statistici_interventii.xlsx',
                                    'Proces_verbal_sedinta.docx','Plan_actiuni_2026.pdf',
                                    'Lista_echipamente.xlsx','Instructiune_operationala.pdf',
                                    'Raport_incident_089.docx','Buget_estimativ.xlsx'];
    v_ip            text[] := ARRAY['10.20.4.17','10.20.4.31','10.20.7.102','10.20.11.5','10.20.4.88'];
BEGIN

    -- ── Contul sursă pentru parolă ─────────────────────────────────────────
    SELECT "PasswordHash" INTO v_hash FROM "Users" WHERE "Username" = v_sursa_parola;

    IF v_hash IS NULL THEN
        RAISE EXCEPTION
            'Contul sursa "%" nu exista. Modifica v_sursa_parola cu username-ul unui cont real.',
            v_sursa_parola;
    END IF;

    -- ── Curățenie: doar datele demo anterioare ─────────────────────────────
    -- Ordinea respectă cheile străine. AuditLogs nu are FK către Users, deci se
    -- șterge după username, nu prin cascadă.
    DELETE FROM "AuditLogs"
     WHERE "Username" LIKE '%.demo';

    -- Destinatarii demo întâi (FK Restrict pe UserId), apoi transferurile demo;
    -- rândurile lor din TransferRecipients se șterg în cascadă.
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

    DELETE FROM "InternalDocumentRecipients"
     WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

    DELETE FROM "InternalDocuments"
     WHERE "AuthorId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');

    UPDATE "OrgUnits" SET "HeadUserId" = NULL WHERE "Code" LIKE 'DEMO-%';

    DELETE FROM "Users" WHERE "Username" LIKE '%.demo';

    -- De jos în sus: cheia străină ParentId e RESTRICT, verificată pe rând.
    DELETE FROM "OrgUnits" WHERE "Code" LIKE 'DEMO-%' AND "Type" = 3;
    DELETE FROM "OrgUnits" WHERE "Code" LIKE 'DEMO-%' AND "Type" = 2;
    DELETE FROM "OrgUnits" WHERE "Code" LIKE 'DEMO-%';

    -- ── 0. Structura organizatorică demo ───────────────────────────────────
    -- Cinci direcții (Code DEMO-1…5, după care le recunoaște curățenia de mai
    -- sus), iar prima are o secție și un serviciu, ca distribuția „cu
    -- subunități” să aibă ce demonstra.
    v_unit_ids := ARRAY[]::uuid[];
    FOR v_i IN 1..5 LOOP
        v_id := gen_random_uuid();
        INSERT INTO "OrgUnits" ("Id", "Name", "Code", "Type", "ParentId", "IsActive", "CreatedAt")
        VALUES (v_id, v_directii[v_i] || ' (demo)', 'DEMO-' || v_i, 1, NULL, TRUE, v_now);
        v_unit_ids := array_append(v_unit_ids, v_id);
    END LOOP;

    v_id := gen_random_uuid();
    INSERT INTO "OrgUnits" ("Id", "Name", "Code", "Type", "ParentId", "IsActive", "CreatedAt")
    VALUES (v_id, 'Sectia investigatii (demo)', 'DEMO-S1', 2, v_unit_ids[1], TRUE, v_now);
    v_unit_ids := array_append(v_unit_ids, v_id);   -- [6]

    INSERT INTO "OrgUnits" ("Id", "Name", "Code", "Type", "ParentId", "IsActive", "CreatedAt")
    VALUES (gen_random_uuid(), 'Serviciul analiza (demo)', 'DEMO-V1', 3, v_id, TRUE, v_now);

    -- ── 1. Utilizatori ─────────────────────────────────────────────────────
    -- 15 conturi: 1 administrator, 2 șefi de direcție, 12 utilizatori.
    -- Sufixul '.demo' e ce le face recunoscute la o rulare ulterioară.

    v_user_ids := ARRAY[]::uuid[];

    FOR v_i IN 1..15 LOOP
        v_id := gen_random_uuid();

        INSERT INTO "Users" (
            "Id", "Username", "Email", "PasswordHash", "FullName", "OrgUnitId",
            "Role", "IsActive", "CreatedAt",
            "FailedLoginAttempts", "TwoFactorEnabled", "TwoFactorChallengeAttempts",
            "LastLoginAt"
        ) VALUES (
            v_id,
            lower(v_prenume[v_i]) || '.' || lower(v_nume[v_i]) || '.demo',
            lower(v_prenume[v_i]) || '.' || lower(v_nume[v_i]) || '@mai.demo',
            v_hash,
            v_prenume[v_i] || ' ' || v_nume[v_i],
            v_unit_ids[1 + (v_i % 5)],
            CASE WHEN v_i = 1 THEN 3 WHEN v_i IN (2,3) THEN 2 ELSE 1 END,
            -- Doi utilizatori dezactivați, ca filtrul „doar activi” din pagina de
            -- utilizatori să aibă ce filtra.
            v_i NOT IN (14, 15),
            v_now - (interval '1 day' * (200 - v_i * 7)),
            0,
            -- 2FA activat doar pe o parte. Alerta „cont privilegiat fără 2FA” de
            -- pe /admin trebuie să aibă pe ce să se declanșeze: administratorul
            -- rămâne intenționat fără.
            v_i IN (2, 4, 5, 9),
            0,
            v_now - (interval '1 hour' * v_i)
        );

        v_user_ids := array_append(v_user_ids, v_id);
    END LOOP;

    v_admin := v_user_ids[1];
    v_sef1  := v_user_ids[2];
    v_sef2  := v_user_ids[3];

    -- Șefii: cei doi șefi de direcție conduc direcțiile lor; un utilizator
    -- obișnuit conduce secția — șeful e dat de unitatea condusă, nu de rol.
    UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[2]
     WHERE "Id" = (SELECT "OrgUnitId" FROM "Users" WHERE "Id" = v_user_ids[2]);
    UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[3]
     WHERE "Id" = (SELECT "OrgUnitId" FROM "Users" WHERE "Id" = v_user_ids[3]);
    UPDATE "Users" SET "OrgUnitId" = v_unit_ids[6] WHERE "Id" = v_user_ids[6];
    UPDATE "OrgUnits" SET "HeadUserId" = v_user_ids[6] WHERE "Id" = v_unit_ids[6];

    -- Un cont blocat chiar acum, pentru alerta corespunzătoare.
    UPDATE "Users"
       SET "FailedLoginAttempts" = 5,
           "LockoutEndsAt"       = v_now + interval '12 minutes',
           "LastFailedLoginAt"   = v_now - interval '3 minutes'
     WHERE "Id" = v_user_ids[7];

    -- ── 2. Transferuri ─────────────────────────────────────────────────────
    -- 40 de transferuri, distribuite pe toate stările.

    FOR v_i IN 1..40 LOOP
        v_sender    := v_user_ids[1 + (v_i % 15)];
        v_recipient := v_user_ids[1 + ((v_i + 6) % 15)];

        IF v_sender = v_recipient THEN
            CONTINUE;   -- nimeni nu-și trimite fișiere sieși
        END IF;

        v_created := v_now - (interval '1 hour' * (v_i * 5));

        -- 0 Pending, 1 Downloaded, 2 Expired, 3 Revoked
        v_status := CASE
            WHEN v_i % 10 = 0 THEN 3
            WHEN v_i % 7  = 0 THEN 2
            WHEN v_i % 3  = 0 THEN 0
            ELSE 1
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
            v_fisiere[1 + (v_i % 10)],
            -- Gol pentru stările terminale: obiectul nu mai există în depozit,
            -- iar o cheie care arată către nimic ar produce 404 la descărcare.
            CASE WHEN v_status = 0
                 THEN 'transfers/demo/' || gen_random_uuid() || '.enc'
                 ELSE '' END,
            (50000 + v_i * 137000)::bigint,
            (50000 + v_i * 137000 + 4096)::bigint,
            encode(sha256(('demo-' || v_i)::bytea), 'hex'),
            v_status,
            v_created,
            v_created + interval '7 days',
            false,
            CASE WHEN v_status = 3 THEN v_created + interval '20 minutes' END,
            CASE WHEN v_status = 3 THEN 'Versiune gresita a documentului' END,
            v_i % 4,          -- toate cele patru categorii
            v_i % 3 = 0       -- o parte permit redistribuirea
        );

        -- Destinatarul direct. Dovada de primire: majoritatea validă, una
        -- invalidă ca să apară în alerte și în filtrul ATENȚIE al jurnalului.
        INSERT INTO "TransferRecipients" (
            "TransferId", "UserId", "EncryptedKeyForUser", "ForwardedById",
            "SentAt", "DownloadedAt", "SignatureValid"
        ) VALUES (
            v_tid, v_recipient, '', NULL, v_created,
            CASE WHEN v_status IN (0, 1) AND (v_status = 1 OR v_i % 5 = 0)
                 THEN v_created + interval '3 hours' END,
            CASE WHEN v_status IN (0, 1) AND (v_status = 1 OR v_i % 5 = 0)
                 THEN (v_i <> 8) END
        );

        -- Al doilea destinatar la fiecare al cincilea transfer: la cele în
        -- așteptare, primul a confirmat și al doilea nu — exact cazul pe care
        -- dovada de primire per destinatar trebuie să-l arate.
        v_recipient2 := v_user_ids[1 + ((v_i + 9) % 15)];

        IF v_i % 5 = 0 AND v_recipient2 NOT IN (v_sender, v_recipient) THEN
            INSERT INTO "TransferRecipients" (
                "TransferId", "UserId", "EncryptedKeyForUser", "ForwardedById",
                "SentAt", "DownloadedAt", "SignatureValid"
            ) VALUES (
                v_tid, v_recipient2, '', NULL, v_created,
                CASE WHEN v_status = 1 THEN v_created + interval '5 hours' END,
                CASE WHEN v_status = 1 THEN true END
            );
        END IF;
    END LOOP;

    -- ── 3. Documente cu versiuni ───────────────────────────────────────────

    FOR v_i IN 1..12 LOOP
        v_id := gen_random_uuid();

        INSERT INTO "Documents" (
            "Id", "Title", "DocumentNumber", "Category", "Keywords",
            "CurrentVersion", "CreatedById", "CreatedAt"
        ) VALUES (
            v_id,
            (ARRAY['Regulament intern de ordine','Procedura de acces in sediu',
                   'Instructiune privind protectia datelor','Plan de continuitate operationala',
                   'Norme de securitate informationala','Ghid de raportare a incidentelor',
                   'Politica de retentie a documentelor','Procedura de arhivare',
                   'Regulament privind transferul de fisiere','Instructiune de utilizare SGDM',
                   'Plan anual de instruire','Norme de clasificare a informatiei'])[v_i],
            'MAI-' || to_char(v_now, 'YYYY') || '-' || lpad(v_i::text, 4, '0'),
            (ARRAY['Regulament','Procedura','Instructiune','Plan','Norma'])[1 + (v_i % 5)],
            'securitate, intern, mai, documentatie',
            1 + (v_i % 3),
            v_user_ids[1 + (v_i % 15)],
            v_now - (interval '1 day' * v_i * 9)
        );

        FOR v_status IN 1..(1 + (v_i % 3)) LOOP
            INSERT INTO "DocumentVersions" (
                "Id", "DocumentId", "VersionNumber", "EncryptedStoragePath",
                "ChecksumSHA256", "CreatedBy", "CreatedAt", "ChangeNotes"
            ) VALUES (
                gen_random_uuid(),
                v_id,
                v_status,
                'documents/demo/' || v_id || '/v' || v_status || '.enc',
                encode(sha256(('doc-' || v_i || '-' || v_status)::bytea), 'hex'),
                (SELECT "Username" FROM "Users" WHERE "Id" = v_user_ids[1 + (v_i % 15)]),
                v_now - (interval '1 day' * (v_i * 9 - v_status * 2)),
                CASE v_status
                    WHEN 1 THEN 'Versiune initiala'
                    WHEN 2 THEN 'Corectii dupa avizare juridica'
                    ELSE 'Actualizare conform noilor norme'
                END
            );
        END LOOP;
    END LOOP;

    -- ── 4. Jurnal de audit ─────────────────────────────────────────────────
    -- ~400 de rânduri pe toate acțiunile și toate rezultatele. Fără ele, pagina
    -- de audit și filtrele arată goale exact în momentul demonstrației.
    --
    -- Result: 0 Success, 1 Failure, 2 Warning
    -- Action: 0 Login, 1 Logout, 2 FileUpload, 3 FileDownload, 4 DocumentCreate,
    --         5 DocumentNewVersion, 6 UserCreated, 7 UserUpdated, 8 FileDeleted,
    --         9 TransferExpired, 10 TransferRevoked, 11 SessionRevoked

    FOR v_i IN 1..400 LOOP
        v_id := v_user_ids[1 + (v_i % 15)];

        INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                                 "Result", "IpAddress", "Timestamp")
        SELECT
            gen_random_uuid(),
            v_id,
            u."Username",
            CASE
                WHEN v_i % 17 = 0 THEN 10
                WHEN v_i % 13 = 0 THEN 11
                WHEN v_i % 11 = 0 THEN 7
                WHEN v_i % 7  = 0 THEN 4
                WHEN v_i % 5  = 0 THEN 3
                WHEN v_i % 3  = 0 THEN 2
                WHEN v_i % 2  = 0 THEN 1
                ELSE 0
            END,
            CASE
                WHEN v_i % 17 = 0 THEN 'Transfer retras ''' || v_fisiere[1 + (v_i % 10)] || ''' inainte de descarcare'
                WHEN v_i % 13 = 0 THEN 'Sesiune inchisa de la distanta (Chrome pe Windows)'
                WHEN v_i % 11 = 0 THEN 'Date de profil actualizate'
                WHEN v_i % 7  = 0 THEN 'Document creat MAI-2026-' || lpad((v_i % 12 + 1)::text, 4, '0')
                WHEN v_i % 5  = 0 THEN 'Fisier descarcat si decriptat ''' || v_fisiere[1 + (v_i % 10)] || ''', semnatura expeditorului VALIDA'
                WHEN v_i % 3  = 0 THEN 'Fisier incarcat ''' || v_fisiere[1 + (v_i % 10)] || ''''
                WHEN v_i % 2  = 0 THEN 'Delogare, sesiune inchisa'
                WHEN v_i % 23 = 0 THEN 'Parola incorecta (2/5)'
                ELSE 'Autentificare reusita'
            END,
            CASE
                WHEN v_i % 17 = 0 OR v_i % 13 = 0 THEN 2   -- Warning
                WHEN v_i % 19 = 0                 THEN 1   -- Failure
                ELSE 0                                      -- Success
            END,
            v_ip[1 + (v_i % 5)],
            v_now - (interval '17 minutes' * v_i)
          FROM "Users" u WHERE u."Id" = v_id;
    END LOOP;

    -- Rânduri anume, pentru alertele de pe /admin ──────────────────────────
    -- Fără ele, panoul de alerte e gol și nu se poate demonstra.

    -- Șase eșecuri consecutive pe același cont: declanșează alerta de forțare.
    FOR v_i IN 1..6 LOOP
        INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                                 "Result", "IpAddress", "Timestamp")
        SELECT gen_random_uuid(), u."Id", u."Username", 0,
               'Parola incorecta (' || v_i || '/5)', 1, '10.20.99.14',
               v_now - (interval '4 minutes' * v_i)
          FROM "Users" u WHERE u."Id" = v_user_ids[7];
    END LOOP;

    -- Autentificare cu cod de recuperare.
    INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                             "Result", "IpAddress", "Timestamp")
    SELECT gen_random_uuid(), u."Id", u."Username", 0,
           'Autentificare cu COD DE RECUPERARE 2FA (7 ramase)', 2, '10.20.4.31',
           v_now - interval '2 days'
      FROM "Users" u WHERE u."Id" = v_sef1;

    -- Semnătură invalidă la descărcare: alerta cea mai gravă din panou.
    INSERT INTO "AuditLogs" ("Id", "UserId", "Username", "Action", "Details",
                             "Result", "IpAddress", "Timestamp")
    SELECT gen_random_uuid(), u."Id", u."Username", 3,
           'Fisier descarcat ''Nota_informativa_securitate.pdf'', semnatura expeditorului INVALIDA',
           2, '10.20.7.102', v_now - interval '9 hours'
      FROM "Users" u WHERE u."Id" = v_sef2;

    RAISE NOTICE 'Seed complet: 15 utilizatori, ~40 transferuri, 12 documente, ~410 randuri de audit.';
    RAISE NOTICE 'Toti utilizatorii demo au parola contului "%".', v_sursa_parola;
    RAISE NOTICE 'Conturi: *.demo — administrator: %', (SELECT "Username" FROM "Users" WHERE "Id" = v_admin);

END $$;

-- ── Verificare ─────────────────────────────────────────────────────────────
-- SELECT "Username", "Role", "IsActive", "TwoFactorEnabled" FROM "Users" WHERE "Username" LIKE '%.demo' ORDER BY "Role" DESC;
-- SELECT "Status", count(*) FROM "FileTransfers" GROUP BY "Status" ORDER BY 1;
-- SELECT "Result", count(*) FROM "AuditLogs" GROUP BY "Result" ORDER BY 1;

-- ── Ștergerea completă a datelor demo ──────────────────────────────────────
-- Rulează blocul de curățenie de la începutul scriptului, sau pe scurt:
--
--   DELETE FROM "AuditLogs" WHERE "Username" LIKE '%.demo';
--   DELETE FROM "TransferRecipients" WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');
--   DELETE FROM "FileTransfers" WHERE "SenderId" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');
--   DELETE FROM "DocumentVersions" WHERE "DocumentId" IN (SELECT "Id" FROM "Documents" WHERE "CreatedById" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo'));
--   DELETE FROM "Documents" WHERE "CreatedById" IN (SELECT "Id" FROM "Users" WHERE "Username" LIKE '%.demo');
--   DELETE FROM "Users" WHERE "Username" LIKE '%.demo';
