#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
# SGDM - backup, verificare și restaurare
#
#   sgdm-backup backup [--database-only]     backup complet (implicit)
#   sgdm-backup list                         backupurile existente
#   sgdm-backup verify  <nume>               verifică sumele SHA-256
#   sgdm-backup restore <nume> --yes [--database-only | --storage-only]
#                                    [--skip-policies]
#   sgdm-backup counts                       rânduri per tabel în baza configurată
#
# Rulat prin compose (din rădăcina repo-ului, un singur rând):
#   docker compose --profile backup run --rm backup
#   docker compose --profile backup run --rm backup restore 20260921-203000 --yes
#
# Ce conține un backup (./backups/<AAAALLZZ-HHMMSS>/):
#   database.dump[.gpg]  pg_dump format custom, schema aplicației
#   storage/             copia bucketului MinIO (versiunile curente)
#   MANIFEST.txt         ce, când, de unde, ce id de cheie principală era activ
#   SHA256SUMS           sumele tuturor fișierelor de mai sus
#
# Ce NU conține, intenționat: cheile. Nici MAI_STORAGE_MASTER_KEYS, nici
# MAI_ARGON2_PEPPER, MAI_TWOFACTOR_KEY sau BACKUP_PASSPHRASE. Un backup care
# își poartă cheile cu el e un backup necriptat. Ele se păstrează separat, în
# afara serverului - detalii în docs/DEPLOYMENT.md, secțiunea Backup.
# ─────────────────────────────────────────────────────────────────────────────
set -eu

BACKUP_ROOT="${BACKUP_ROOT:-/backups}"
BUCKET="${STORAGE_BUCKET:-mai-secure}"
STORAGE_ENDPOINT="${STORAGE_ENDPOINT:-http://minio:9000}"
PG_SCHEMA="${BACKUP_PG_SCHEMA:-public}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"

log()  { echo "[sgdm-backup] $*"; }
fail() { echo "[sgdm-backup] EROARE: $*" >&2; exit 1; }

# ── Conexiunea la bază ───────────────────────────────────────────────────────
# API-ul folosește formatul Npgsql („Host=...;Username=...;SSL Mode=Require”),
# pg_dump înțelege altceva. În loc de o a doua variabilă (care ar ajunge să
# arate spre altă bază decât API-ul), același DB_CONNECTION_STRING se traduce
# aici în variabilele PG* standard ale libpq.
#
# Limitare cunoscută: o parolă care conține „;” (Npgsql o acceptă între
# ghilimele) nu se poate parsa aici. Scriptul o detectează și se oprește.
parse_connection_string() {
    [ -n "${DB_CONNECTION_STRING:-}" ] || fail "DB_CONNECTION_STRING lipseste din .env."

    case "$DB_CONNECTION_STRING" in
        *\"*|*\'*) fail "DB_CONNECTION_STRING contine ghilimele (parola cu ';'?). Alegeti o parola fara ';' si ghilimele." ;;
    esac

    old_ifs=$IFS
    IFS=';'
    set -f
    for pair in $DB_CONNECTION_STRING; do
        case "$pair" in *=*) ;; *) continue ;; esac
        key=$(printf '%s' "${pair%%=*}" | tr -d ' ' | tr '[:upper:]' '[:lower:]')
        val=$(printf '%s' "${pair#*=}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
        case "$key" in
            host|server)                   export PGHOST="$val" ;;
            port)                          export PGPORT="$val" ;;
            database|db|initialcatalog)    export PGDATABASE="$val" ;;
            username|userid|user|uid)      export PGUSER="$val" ;;
            password|pwd)                  export PGPASSWORD="$val" ;;
            sslmode)
                # Npgsql: VerifyFull / VerifyCA → libpq: verify-full / verify-ca
                mode=$(printf '%s' "$val" | tr '[:upper:]' '[:lower:]')
                case "$mode" in
                    verifyfull) mode=verify-full ;;
                    verifyca)   mode=verify-ca ;;
                esac
                export PGSSLMODE="$mode" ;;
        esac
    done
    set +f
    IFS=$old_ifs

    [ -n "${PGHOST:-}" ]     || fail "DB_CONNECTION_STRING nu contine Host."
    [ -n "${PGDATABASE:-}" ] || fail "DB_CONNECTION_STRING nu contine Database."
    [ -n "${PGUSER:-}" ]     || fail "DB_CONNECTION_STRING nu contine Username."

    # Supabase cere TLS. Dacă șirul nu spune nimic, libpq ar încerca „prefer”,
    # adică ar accepta și o conexiune în clar dacă cineva o impune pe drum.
    case "$PGHOST" in
        *.supabase.co|*.supabase.com) : "${PGSSLMODE:=require}"; export PGSSLMODE ;;
    esac
}

storage_alias() {
    [ -n "${MINIO_ROOT_USER:-}" ] && [ -n "${MINIO_ROOT_PASSWORD:-}" ] \
        || fail "MINIO_ROOT_USER / MINIO_ROOT_PASSWORD lipsesc din .env."
    # Configurația mc stă în /tmp: containerul e efemer, iar credențialele
    # nu trebuie să ajungă în volumul ./backups.
    export MC_CONFIG_DIR=/tmp/.mc
    mc alias set sgdm "$STORAGE_ENDPOINT" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null \
        || fail "Nu ma pot conecta la MinIO ($STORAGE_ENDPOINT)."
}

gpg_prepare() {
    export GNUPGHOME=/tmp/.gnupg
    mkdir -p -m 700 "$GNUPGHOME"
}

# ═════════════════════════════════════════════════════════════════════════════
# backup
# ═════════════════════════════════════════════════════════════════════════════
cmd_backup() {
    with_storage=1
    for arg in "$@"; do
        case "$arg" in
            # Folosit la migrarea bazei (scripts/migrate-db-to-docker.ps1):
            # stocarea rămâne în același MinIO, doar baza își schimbă locul.
            --database-only) with_storage=0 ;;
            *) fail "Optiune necunoscuta: $arg" ;;
        esac
    done

    parse_connection_string
    if [ "$with_storage" -eq 1 ]; then storage_alias; fi

    name=$(date -u +%Y%m%d-%H%M%S)
    final="$BACKUP_ROOT/$name"
    # Se scrie într-un director temporar și se redenumește abia la final.
    # Un backup întrerupt (disc plin, rețea căzută) nu arată niciodată ca unul
    # complet, iar „restore” refuză orice director fără SHA256SUMS.
    work="$BACKUP_ROOT/.incomplete-$name"
    mkdir -p "$work/storage"

    log "Backup $name"
    log "Baza: $PGUSER@$PGHOST:${PGPORT:-5432}/$PGDATABASE (schema $PG_SCHEMA, sslmode ${PGSSLMODE:-implicit})"

    # --format=custom: comprimat, restaurabil selectiv, verificabil cu pg_restore -l.
    # --no-owner / --no-privileges: rolurile Supabase (postgres, authenticated,
    #   anon) nu există pe un Postgres local; restaurarea nu depinde de ele.
    # --schema: doar schema aplicației. Pe Supabase, restul (auth, storage,
    #   realtime) aparține platformei și nu se restaurează de mână.
    # --no-publications / --no-subscriptions: replicarea logică ține de
    #   serverul sursă (Supabase are publicația „supabase_realtime”); pe altă
    #   bază ar eșua sau ar crea obiecte fără sens.
    pg_dump --format=custom --compress=6 --no-owner --no-privileges \
            --no-publications --no-subscriptions \
            --schema="$PG_SCHEMA" --file="$work/database.dump" \
        || fail "pg_dump a esuat. Pe Supabase folositi pooler-ul in mod SESSION (port 5432), nu TRANSACTION (6543)."

    # Validare imediată: un dump care nu se poate lista nu se poate nici restaura.
    tables=$(pg_restore --list "$work/database.dump" | grep -c ' TABLE DATA ' || true)
    log "Baza: $tables tabele cu date"

    # Dump-ul conține hashuri de parole (Argon2id, dar cu pepper-ul în afara
    # bazei), cheile private E2EE (cifrate cu parola fiecărui utilizator),
    # secretele TOTP (cifrate cu MAI_TWOFACTOR_KEY), numele fișierelor și tot
    # jurnalul de audit. Nimic direct exploatabil, dar suficient pentru o hartă
    # completă a cine cu cine comunică. Cu BACKUP_PASSPHRASE setat, se cifrează.
    dump_file="database.dump"
    if [ -n "${BACKUP_PASSPHRASE:-}" ]; then
        gpg_prepare
        printf '%s' "$BACKUP_PASSPHRASE" | gpg --batch --yes --quiet --pinentry-mode loopback \
            --passphrase-fd 0 --symmetric --cipher-algo AES256 \
            --output "$work/database.dump.gpg" "$work/database.dump"
        rm -f "$work/database.dump"
        dump_file="database.dump.gpg"
        log "Baza: dump cifrat (gpg, AES-256)"
    else
        log "ATENTIE: BACKUP_PASSPHRASE nu e setat - dump-ul bazei ramane necifrat."
    fi

    # Obiectele din MinIO sunt deja cifrate: transfers/ în browser (E2EE),
    # documents/ și internal/ de API, cu cheia principală. Copia lor nu mai
    # are nevoie de o a doua cifrare - dar nici nu se poate citi fără chei.
    # --preserve păstrează data modificării și metadatele obiectelor.
    if [ "$with_storage" -eq 1 ]; then
        mc mirror --quiet --preserve "sgdm/$BUCKET" "$work/storage" >/dev/null \
            || fail "Copierea bucketului $BUCKET a esuat."
        objects=$(find "$work/storage" -type f | wc -l | tr -d ' ')
        size=$(du -sh "$work/storage" | cut -f1)
        log "Stocare: $objects obiecte ($size) din bucketul $BUCKET"
    else
        rmdir "$work/storage"
        objects="nesalvat (--database-only)"
        log "Stocare: omisa (--database-only)"
    fi

    {
        echo "SGDM backup $name"
        echo "Creat (UTC):        $(date -u '+%Y-%m-%d %H:%M:%S')"
        echo "Baza:               $PGHOST/$PGDATABASE, schema $PG_SCHEMA"
        echo "pg_dump:            $(pg_dump --version)"
        echo "Tabele cu date:     $tables"
        echo "Dump:               $dump_file"
        echo "Bucket:             $BUCKET ($objects)"
        echo "Cheie stocare activa: ${STORAGE_ENCRYPTION_ACTIVE_KEY:-necunoscut}"
        echo ""
        echo "Pentru restaurare sunt necesare, din afara acestui director:"
        echo "  - MAI_STORAGE_MASTER_KEYS (toate id-urile folosite pana la aceasta data)"
        echo "  - MAI_ARGON2_PEPPER, MAI_TWOFACTOR_KEY, MAI_JWT_KEY"
        [ "$dump_file" = "database.dump.gpg" ] && echo "  - BACKUP_PASSPHRASE"
    } > "$work/MANIFEST.txt"

    # Sumele se calculează pe tot ce e în director, cu căi relative, ca
    # verificarea să funcționeze și după copierea backupului pe alt disc.
    (cd "$work" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)

    mv "$work" "$final"
    log "Gata: $final"

    prune
}

# Păstrează ultimele BACKUP_RETENTION_DAYS zile. Șterge doar directoare cu
# numele în formatul generat de acest script; orice altceva din ./backups
# (o copie manuală, un README) rămâne neatins.
prune() {
    case "$RETENTION_DAYS" in
        ''|*[!0-9]*) fail "BACKUP_RETENTION_DAYS trebuie sa fie un numar (0 = fara stergere)." ;;
    esac
    [ "$RETENTION_DAYS" -gt 0 ] || return 0

    find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d \
         -name '20[0-9][0-9][01][0-9][0-3][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]' \
         -mtime +"$RETENTION_DAYS" | while read -r old; do
        log "Retentie: sterg $(basename "$old") (mai vechi de $RETENTION_DAYS zile)"
        rm -rf "$old"
    done

    # Resturi de la backupuri întrerupte, mai vechi de o zi.
    find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -name '.incomplete-*' -mtime +0 \
         -exec rm -rf {} + 2>/dev/null || true
}

# ═════════════════════════════════════════════════════════════════════════════
# list / verify
# ═════════════════════════════════════════════════════════════════════════════
cmd_list() {
    found=0
    for d in "$BACKUP_ROOT"/20*; do
        [ -d "$d" ] || continue
        found=1
        state="complet"
        [ -f "$d/SHA256SUMS" ] || state="FARA SUME - nu se poate restaura"
        printf '%s  %6s  %s\n' "$(basename "$d")" "$(du -sh "$d" | cut -f1)" "$state"
    done
    [ "$found" -eq 1 ] || log "Niciun backup in $BACKUP_ROOT."
}

resolve_backup() {
    [ -n "${1:-}" ] || fail "Lipseste numele backupului. Vezi: sgdm-backup list"
    case "$1" in */*|*..*) fail "Nume de backup invalid: $1" ;; esac
    dir="$BACKUP_ROOT/$1"
    [ -d "$dir" ] || fail "Nu exista $dir."
    [ -f "$dir/SHA256SUMS" ] || fail "$1 nu are SHA256SUMS (backup incomplet)."
}

cmd_verify() {
    resolve_backup "${1:-}"
    # Detectează atât coruperea pe disc, cât și o modificare deliberată: un
    # fișier de stocare înlocuit, un dump editat. (Sumele nu apără de cineva
    # care rescrie și SHA256SUMS - pentru asta, copia din afara serverului.)
    (cd "$dir" && sha256sum --check --quiet SHA256SUMS) \
        || fail "Backupul $1 NU trece verificarea de integritate."
    log "Backupul $1: integritate verificata ($(wc -l < "$dir/SHA256SUMS" | tr -d ' ') fisiere)."
}

# ═════════════════════════════════════════════════════════════════════════════
# restore
# ═════════════════════════════════════════════════════════════════════════════
cmd_restore() {
    name="${1:-}"
    [ $# -gt 0 ] && shift
    confirm=0; do_db=1; do_storage=1; skip_policies=0
    for arg in "$@"; do
        case "$arg" in
            --yes)           confirm=1 ;;
            --database-only) do_storage=0 ;;
            --storage-only)  do_db=0 ;;
            --skip-policies) skip_policies=1 ;;
            *) fail "Optiune necunoscuta: $arg" ;;
        esac
    done

    # Integritatea ÎNAINTE de orice modificare: o restaurare dintr-un backup
    # corupt distruge datele bune care existau.
    cmd_verify "$name"

    if [ "$confirm" -ne 1 ]; then
        cat "$dir/MANIFEST.txt"
        echo ""
        fail "Restaurarea SUPRASCRIE datele curente. Repetati comanda cu --yes."
    fi

    if [ "$do_db" -eq 1 ]; then
        parse_connection_string
        dump="$dir/database.dump"
        if [ -f "$dir/database.dump.gpg" ]; then
            [ -n "${BACKUP_PASSPHRASE:-}" ] || fail "Dump-ul e cifrat; lipseste BACKUP_PASSPHRASE."
            gpg_prepare
            dump=/tmp/database.dump
            printf '%s' "$BACKUP_PASSPHRASE" | gpg --batch --yes --quiet --pinentry-mode loopback \
                --passphrase-fd 0 --decrypt --output "$dump" "$dir/database.dump.gpg" \
                || fail "Decriptarea dump-ului a esuat (parola gresita sau fisier alterat)."
        fi

        log "Restaurez baza in $PGHOST/$PGDATABASE (schema $PG_SCHEMA)..."
        # --clean --if-exists: tabelele existente se înlocuiesc cu cele din dump.
        # --single-transaction: totul sau nimic. O restaurare care pică la
        #   jumătate nu lasă o bază cu jumătate din tabele vechi, jumătate noi.
        # --skip-policies: la mutarea de pe Supabase. Politicile RLS de acolo
        # sunt scrise pentru rolurile platformei (anon, authenticated), care pe
        # un PostgreSQL obișnuit nu există; restaurarea lor ar eșua și ar anula
        # toată tranzacția. Aplicația nu folosește RLS: accesul îl decide API-ul.
        # Lista de conținut se editează, nu dump-ul: se omit doar intrările
        # POLICY, restul se restaurează exact.
        list_opt=""
        if [ "$skip_policies" -eq 1 ]; then
            pg_restore --list "$dump" | grep -v ' POLICY ' > /tmp/restore.list
            skipped=$(pg_restore --list "$dump" | grep -c ' POLICY ' || true)
            log "Omit $skipped politici RLS (--skip-policies)."
            list_opt="--use-list=/tmp/restore.list"
        fi

        # shellcheck disable=SC2086
        pg_restore --clean --if-exists --no-owner --no-privileges \
                   --single-transaction --exit-on-error $list_opt \
                   --schema="$PG_SCHEMA" --dbname="$PGDATABASE" "$dump" \
            || fail "pg_restore a esuat; baza a ramas neschimbata (tranzactie anulata)."
        rm -f /tmp/database.dump /tmp/restore.list
        log "Baza restaurata."
    fi

    if [ "$do_storage" -eq 1 ] && [ ! -d "$dir/storage" ]; then
        log "Backupul nu contine stocarea (facut cu --database-only); o las neatinsa."
        do_storage=0
    fi

    if [ "$do_storage" -eq 1 ]; then
        storage_alias
        mc mb --ignore-existing "sgdm/$BUCKET" >/dev/null
        log "Restaurez stocarea in bucketul $BUCKET..."
        # Fără --remove: obiectele apărute după backup rămân în bucket. Nu
        # sunt referite din baza restaurată, deci nu apar nicăieri, iar jobul
        # de expirare le curăță pe cele de transfer. Ștergerea automată ar fi
        # ireversibilă dacă s-a ales din greșeală un backup vechi.
        mc mirror --quiet --overwrite --preserve "$dir/storage" "sgdm/$BUCKET" >/dev/null \
            || fail "Restaurarea stocarii a esuat."
        log "Stocare restaurata."
    fi

    log "Restaurare $name terminata. Reporniti API-ul: docker compose restart api"
}

# ═════════════════════════════════════════════════════════════════════════════
# counts - numărul EXACT de rânduri din fiecare tabel al schemei
# ═════════════════════════════════════════════════════════════════════════════
# Folosit la migrare: aceleași numere în sursă și în destinație dovedesc că
# nu s-a pierdut nimic pe drum. count(*) exact, nu estimarea din
# pg_stat_user_tables, care poate fi zero pe o bază abia restaurată.
# Ieșire: „<tabel> <număr>” pe fiecare rând, ordonat după nume.
cmd_counts() {
    parse_connection_string
    psql -X -At -v ON_ERROR_STOP=1 -c "
        SELECT format('SELECT %L, count(*) FROM %I.%I;', table_name, table_schema, table_name)
        FROM information_schema.tables
        WHERE table_schema = '$PG_SCHEMA' AND table_type = 'BASE TABLE'
        ORDER BY table_name" \
    | psql -X -At -F ' ' -v ON_ERROR_STOP=1 \
        || fail "Nu pot citi tabelele din $PGHOST/$PGDATABASE."
}

# ═════════════════════════════════════════════════════════════════════════════
command="${1:-backup}"
[ $# -gt 0 ] && shift
case "$command" in
    backup)  cmd_backup "$@" ;;
    list)    cmd_list ;;
    counts)  cmd_counts ;;
    verify)  cmd_verify "$@" ;;
    restore) cmd_restore "$@" ;;
    -h|--help|help) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//' ;;
    *) fail "Comanda necunoscuta: $command (backup | list | verify | restore | counts)" ;;
esac
