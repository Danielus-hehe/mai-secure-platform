#!/bin/sh
# ═══════════════════════════════════════════════════════════════════════════
# seed-demo-objects.sh — Creeaza fisiere demonstrative in MinIO
#
# Ruleaza DUPA 900_seed_demo.sql si populeaza bucketul cu obiecte reale,
# astfel incat documentele normative sa se poata descarca efectiv.
#
# Transferurile E2EE raman fara obiecte reale (cele in Pending au chei de
# referinta, dar fara criptare reala — vezi comentariile din seed SQL).
#
# Utilizare:
#   docker compose run --rm minio-init sh /scripts/seed-demo-objects.sh
#   sau manual, cu mc configurat.
#
# Idempotent: obiectele se suprascriu la fiecare rulare.
# ═══════════════════════════════════════════════════════════════════════════

set -e

BUCKET="${SGDM_BUCKET:-mai-secure}"
ALIAS="local"
TMP="/tmp/sgdm-seed"
mkdir -p "$TMP"

echo "=== Seed obiecte demonstrative in $BUCKET ==="

# ── Documente normative ────────────────────────────────────────────────────
# Fisiere text simple (.txt) care se pot deschide si descarca.
# In productie ar fi .pdf/.docx; aici e suficient sa demonstreze fluxul.
#
# NOTA: Daca StorageEncryption este activ, aceste fisiere trebuie criptate
# de API (storage:recrypt). Cu AllowPlaintextRead=true (implicit), API-ul
# le livreaza si in clar.

create_doc() {
    local path="$1"
    local title="$2"
    local content="$3"
    local file="$TMP/$(basename "$path")"

    cat > "$file" <<DOCEOF
================================================================================
  MINISTERUL AFACERILOR INTERNE AL REPUBLICII MOLDOVA
  Sistem de Gestiune a Documentelor si Transferurilor Securizate (SGDM)
================================================================================

  $title

  Data: $(date +%Y-%m-%d)
  Clasificare: INTERN
  Versiune: Document demonstrativ

--------------------------------------------------------------------------------

$content

--------------------------------------------------------------------------------
  Acest document este generat automat pentru demonstrarea functionalitatii
  platformei SGDM. Nu reprezinta un document oficial al MAI.
================================================================================
DOCEOF

    mc cp "$file" "$ALIAS/$BUCKET/$path"
    echo "  + $path ($(wc -c < "$file") octeti)"
}

echo ""
echo "── Documente normative (documents/) ──"

# Trebuie sa folosim query-ul din baza ca sa aflam ID-urile exacte.
# Dar scriptul ruleaza din minio-init (fara acces la PG), deci cream
# documente la cai generice pe care le poate folosi si fara baza.
# Seed-ul SQL foloseste aceleasi cai.

for i in 1 2 3 4 5 6 7 8 9 10 11 12; do
    case $i in
        1)  title="Regulament intern de ordine"
            content="Art. 1. Prezentul regulament stabileste normele de ordine interioara.\nArt. 2. Toti angajatii sunt obligati sa respecte programul de lucru.\nArt. 3. Accesul in cladire se face pe baza legitimatiei de serviciu.\nArt. 4. Este interzisa divulgarea informatiilor de serviciu." ;;
        2)  title="Procedura de acces in sediu"
            content="1. Prezentarea legitimatiei la punctul de control.\n2. Inregistrarea in registrul de acces.\n3. Insotirea vizitatorilor pe toata durata prezentei in sediu.\n4. Predarea cheilor si cardurilor la parasirea cladirii." ;;
        3)  title="Instructiune privind protectia datelor"
            content="Scopul: asigurarea conformitatii cu cerintele de protectie a datelor.\n\nMasuri obligatorii:\n- Criptarea fisierelor la transferul intre subdiviziuni\n- Utilizarea parolelor complexe (minim 12 caractere)\n- Activarea autentificarii in doi pasi\n- Raportarea incidentelor in maxim 24 de ore" ;;
        4)  title="Plan de continuitate operationala"
            content="Obiectiv: mentinerea functiilor critice in cazul unui incident major.\n\nScenarii acoperite:\n- Indisponibilitatea sediului principal\n- Pierderea accesului la reteaua interna\n- Compromiterea sistemului informatic\n\nTimp maxim de recuperare: 4 ore pentru functiile critice." ;;
        5)  title="Norme de securitate informationala"
            content="Cap. I - Dispozitii generale\nAceste norme se aplica tuturor angajatilor care utilizeaza sisteme informatice.\n\nCap. II - Clasificarea informatiei\nInformatia se clasifica in: publica, interna, confidentiala, secret de serviciu.\n\nCap. III - Obligatii\nFiecare utilizator raspunde de credentialele proprii de acces." ;;
        6)  title="Ghid de raportare a incidentelor"
            content="Pasul 1: Identificarea incidentului\n- Ce sisteme sunt afectate?\n- Cand a fost observat?\n- Cine a raportat?\n\nPasul 2: Notificarea responsabilului de securitate\nPasul 3: Documentarea actiunilor intreprinse\nPasul 4: Analiza post-incident si lectii invatate" ;;
        7)  title="Politica de retentie a documentelor"
            content="Documente operative: minim 5 ani\nCorespondenta: minim 3 ani\nDocumente financiare: minim 10 ani\nDocumente clasificate: conform legislatiei in vigoare\n\nDupa expirarea termenului, documentele se distrug prin distrugator de documente nivel P-5 sau superior." ;;
        8)  title="Procedura de arhivare"
            content="1. Verificarea completarii dosarelor\n2. Numerotarea filelor si intocmirea opisului\n3. Legarea si sigilarea dosarelor\n4. Predarea la arhiva cu proces-verbal\n5. Inregistrarea in registrul de evidenta a arhivei" ;;
        9)  title="Regulament privind transferul de fisiere"
            content="Transferul de fisiere intre angajatii MAI se realizeaza EXCLUSIV\nprin platforma SGDM.\n\nEste INTERZIS:\n- Trimiterea documentelor de serviciu prin email personal\n- Utilizarea serviciilor de stocare in cloud (Google Drive, Dropbox)\n- Copierea pe dispozitive USB neautorizate\n\nFisierele sunt criptate end-to-end: serverul nu poate citi continutul." ;;
        10) title="Instructiune de utilizare SGDM"
            content="1. Autentificarea se face cu contul de serviciu\n2. La prima autentificare, se genereaza cheile criptografice\n3. Trimiterea fisierelor: butonul Trimite > selectare destinatar > fisier\n4. Primirea: sectiunea Primite > descarcare si verificare semnatura\n5. Probleme? Contactati Directia Tehnologii Informationale" ;;
        11) title="Plan anual de instruire"
            content="Trimestrul I: Securitate informationala (toti angajatii)\nTrimestrul II: Protectia datelor cu caracter personal\nTrimestrul III: Gestionarea incidentelor de securitate\nTrimestrul IV: Evaluare si certificare\n\nFiecare instruire se incheie cu un test de verificare.\nPromovarea: minim 70% raspunsuri corecte." ;;
        12) title="Norme de clasificare a informatiei"
            content="Nivel 1 - PUBLICA: informatii destinate publicului larg\nNivel 2 - INTERNA: informatii destinate angajatilor MAI\nNivel 3 - CONFIDENTIALA: acces restrictionat la persoane autorizate\nNivel 4 - SECRET DE SERVICIU: conform legislatiei\n\nMarcarea se face pe fiecare pagina, in antet si subsol." ;;
    esac

    # Seed-ul SQL pune cheile documentelor la 'documents/demo/{doc_uuid}/v{n}.enc'
    # Dar UUID-urile sunt generate dinamic. Cream documente la cai fixe ca fallback,
    # folosite de seed-ul SQL (v_doc_demo_keys).
    create_doc "documents/demo/doc-$i/v1.enc" "$title" "$content"

    if [ $((i % 3)) -ne 0 ]; then
        create_doc "documents/demo/doc-$i/v2.enc" "$title (v2 - actualizat)" "$content\n\n[Actualizat: corectii dupa avizare juridica]"
    fi
    if [ $((i % 3)) -eq 2 ]; then
        create_doc "documents/demo/doc-$i/v3.enc" "$title (v3 - revizuit)" "$content\n\n[Revizuit: conform noilor norme]"
    fi
done

echo ""
echo "── Documente interne (internal/) ──"

for i in 1 2 3 4 5 6 7 8; do
    case $i in
        1) title="Dispozitie nr. D-2026/0001"; content="Se dispune verificarea accesului in toate subdiviziunile." ;;
        2) title="Nota informativa nr. 14"; content="Se aduce la cunostinta noua procedura de raportare a incidentelor." ;;
        3) title="Circulara privind programul de lucru"; content="Incepand cu data de 01.10.2026, programul de lucru se modifica." ;;
        4) title="Dispozitie nr. D-2026/0015"; content="Se numeste comisia de inventariere a echipamentelor IT." ;;
        5) title="Nota privind instruirea periodica"; content="Toti angajatii sunt obligati sa parcurga modulul de securitate informationala." ;;
        6) title="Circulara - actualizare SGDM"; content="Platforma SGDM a fost actualizata. Noile functionalitati includ documente interne." ;;
        7) title="Dispozitie nr. D-2026/0023"; content="Se aproba procedura de backup si restaurare a datelor." ;;
        8) title="Nota privind auditarea accesului"; content="Se va efectua un audit al accesului la sistemele informatice." ;;
    esac

    create_doc "internal/2026/09/intdoc-$i.enc" "$title" "$content"
done

echo ""
echo "── Fisiere de transfer demonstrative (transfers/) ──"
echo "   Transferurile E2EE nu au obiecte reale in seed (IsEncrypted=false)."
echo "   Cele in starea Pending au StorageKey dar fara cifrotext valid."
echo "   Pentru un transfer real: autentificati-va cu doua conturi si trimiteti un fisier."

# Cream cateva fisiere placeholder pentru transferurile Pending, ca sa nu dea 404
# la o eventuala incercare de descarcare (vor esua la decriptare, dar obiectul exista)
for i in 1 2 3 4 5; do
    file="$TMP/transfer-placeholder-$i.enc"
    echo "SGDM demo transfer placeholder - not a real ciphertext" > "$file"
    mc cp "$file" "$ALIAS/$BUCKET/transfers/demo/placeholder-$i.enc"
    echo "  + transfers/demo/placeholder-$i.enc"
done

echo ""
echo "=== Seed obiecte complet ==="
echo ""
echo "Documente normative: 12 documente, ~25 versiuni"
echo "Documente interne: 8 documente"
echo "Transferuri placeholder: 5"
echo ""
echo "Urmatorul pas: rulati 900_seed_demo.sql in PostgreSQL"
echo "  (sau invers: SQL intai, apoi acest script)"
echo ""

# Curatenie
rm -rf "$TMP"
