# SGDM - mutarea bazei de date de pe Supabase in PostgreSQL-ul din Docker.
#
# Ce face, in ordine (se opreste la prima eroare, fara sa atinga .env):
#   1. verifica: API-ul oprit (altfel datele scrise in timpul mutarii se pierd),
#      parola POSTGRES_PASSWORD (o genereaza daca lipseste), sursa accesibila;
#   2. porneste serviciul postgres si asteapta sa fie sanatos;
#   3. face backup DOAR al bazei din sursa (imaginea de backup, pg_dump);
#   4. il restaureaza in postgres, fara politicile RLS specifice Supabase;
#   5. compara numarul EXACT de randuri din fiecare tabel, sursa vs destinatie;
#   6. doar daca totul corespunde: rescrie DB_CONNECTION_STRING in .env spre
#      baza locala si pastreaza vechea valoare ca SUPABASE_CONNECTION_STRING
#      (pentru revenire). Copie a .env inainte: .env.bak-<data-ora>.
#
# Fisierele din MinIO nu se muta: raman in acelasi bucket, iar randurile din
# baza le refera exact ca inainte.
#
# De ce prin imaginea de backup si nu prin pg_dump pe Windows: nu cere nimic
# instalat pe gazda, iar backup-ul facut aici ramane in .\backups - dovada si
# punct de revenire. Migrarea e totodata un test real al restaurarii.
#
# Fisier doar ASCII (fara diacritice, fara BOM): Windows PowerShell 5.1
# citeste un .ps1 fara BOM in codificarea ANSI.
#
# Exemple (din radacina repo-ului):
#   .\scripts\migrate-db-to-docker.ps1
#   .\scripts\migrate-db-to-docker.ps1 -Source "Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.xxxx;Password=...;SSL Mode=Require"
#   .\scripts\migrate-db-to-docker.ps1 -KeepEnv      (nu modifica .env)

param(
    # Connection string-ul sursei (format Npgsql). Implicit: SUPABASE_CONNECTION_STRING
    # din .env, altfel DB_CONNECTION_STRING daca nu arata deja spre localhost.
    [string]$Source,
    # Face mutarea si verificarea, dar lasa .env neschimbat.
    [switch]$KeepEnv
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$envPath = Join-Path $root '.env'

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "EROARE: $text" -ForegroundColor Red; exit 1 }

function Read-DotEnv {
    $map = @{}
    foreach ($line in [IO.File]::ReadAllLines($envPath, [Text.Encoding]::UTF8)) {
        if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
        $k, $v = $line -split '=', 2
        $map[$k.Trim()] = $v.Trim()
    }
    return $map
}

function Get-ConnPart($conn, $key) {
    foreach ($pair in $conn -split ';') {
        $k, $v = $pair -split '=', 2
        if ($k -and ($k -replace '\s', '') -ieq $key) { return $v.Trim() }
    }
    return $null
}

# Randurile "<tabel> <numar>" scrise de `sgdm-backup counts` -> hashtable.
function Parse-Counts($lines) {
    $h = @{}
    foreach ($l in $lines) { if ($l -match '^(\S+) (\d+)$') { $h[$matches[1]] = [int64]$matches[2] } }
    return $h
}

if (-not (Test-Path $envPath)) { Fail "Nu exista .env in $root." }
$cfg = Read-DotEnv

# -- 1. Verificari -----------------------------------------------------------
Step 'Verificari'

# Un API care scrie in Supabase in timpul mutarii ar pierde exact acele scrieri.
$listening = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
if ($listening) { Fail "Pe portul 5000 ruleaza un API (dotnet run). Opriti-l cu Ctrl+C si rulati din nou." }
# Fara '2>$null' la comenzile docker: in Windows PowerShell 5.1, cu
# ErrorActionPreference=Stop, orice linie scrisa de docker pe stderr (inclusiv
# mesajele de progres) ar deveni o eroare care opreste scriptul.
docker compose stop web api
Write-Host 'API-ul si serviciul web din Docker: oprite pe durata mutarii.'

# Sursa
if (-not $Source) { $Source = $cfg['SUPABASE_CONNECTION_STRING'] }
if (-not $Source) {
    $current = $cfg['DB_CONNECTION_STRING']
    $h = Get-ConnPart $current 'Host'
    if ($current -and $h -and $h -notmatch '^(localhost|127\.0\.0\.1|postgres)$') { $Source = $current }
}
if (-not $Source) {
    Fail "Nu am gasit sursa. DB_CONNECTION_STRING arata deja spre baza locala? Dati sursa cu -Source `"...`"."
}
$srcHost = Get-ConnPart $Source 'Host'
Write-Host "Sursa: $srcHost / $(Get-ConnPart $Source 'Database')"

# Conexiunea directa Supabase (db.<proiect>.supabase.co) are doar IPv6, iar
# containerele Docker nu au IPv6. Esecul ar veni dupa un timeout lung si cu un
# mesaj neclar; mai bine acum, cu solutia.
if ($srcHost -match '^db\..+\.supabase\.co$') {
    Fail ("Conexiunea directa Supabase ($srcHost) e doar IPv6, iar containerul nu o poate folosi. " +
          "Din Supabase: Settings -> Database -> Connection string -> Session pooler (port 5432), " +
          "apoi rulati cu -Source `"<acel string>`".")
}
if ((Get-ConnPart $Source 'Port') -eq '6543') {
    Fail 'Portul 6543 e pooler-ul Supabase in mod Transaction, care nu suporta pg_dump. Folositi Session pooler (port 5432).'
}

# Parola bazei locale: generata daca lipseste. Doar litere si cifre - ajunge
# intr-un connection string, unde ';' sau ghilimelele ar rupe formatul.
$pw = $cfg['POSTGRES_PASSWORD']
if (-not $pw -or $pw -match 'SCHIMBA_MA|GENERATI_') {
    $existingVolume = docker volume ls --format '{{.Name}}' | Where-Object { $_ -match '_pg-data$' }
    if ($existingVolume) {
        Fail ("Volumul bazei ($existingVolume) exista deja, dar POSTGRES_PASSWORD lipseste din .env. " +
              "PostgreSQL foloseste parola doar la prima initializare; completati parola folosita atunci.")
    }
    $pw = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    $text = [IO.File]::ReadAllText($envPath, [Text.Encoding]::UTF8)
    if ($text -match '(?m)^POSTGRES_PASSWORD=[^\r\n]*') {
        $text = [regex]::Replace($text, '(?m)^POSTGRES_PASSWORD=[^\r\n]*', "POSTGRES_PASSWORD=$pw")
    } else {
        $text = $text.TrimEnd() + "`r`nPOSTGRES_PASSWORD=$pw`r`n"
    }
    [IO.File]::WriteAllText($envPath, $text, (New-Object Text.UTF8Encoding $false))
    Write-Host 'POSTGRES_PASSWORD lipsea: am generat una (32 de caractere) si am scris-o in .env.' -ForegroundColor Yellow
} elseif ($pw -notmatch '^[A-Za-z0-9]+$') {
    Fail 'POSTGRES_PASSWORD trebuie sa contina doar litere si cifre (ajunge intr-un connection string).'
}

# Imaginea de backup se reconstruieste intotdeauna: compose nu o reface
# singur daca exista deja, iar o imagine veche nu are comenzile folosite mai
# jos (counts, --skip-policies). Din cache dureaza cateva secunde.
Step 'Construiesc imaginea de backup'
docker compose --profile backup build backup
if ($LASTEXITCODE -ne 0) { Fail 'Imaginea de backup nu s-a putut construi.' }

# -- 2. PostgreSQL local -----------------------------------------------------
Step 'Pornesc PostgreSQL in Docker'
docker compose up -d postgres
if ($LASTEXITCODE -ne 0) { Fail 'Serviciul postgres nu a pornit (portul 5432 ocupat? vezi POSTGRES_HOST_PORT).' }

$healthy = $false
for ($i = 0; $i -lt 30; $i++) {
    $state = docker inspect --format '{{.State.Health.Status}}' sgdm-postgres
    if ($state -eq 'healthy') { $healthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healthy) { Fail 'PostgreSQL nu a devenit sanatos in 60 de secunde: docker compose logs postgres' }
Write-Host 'PostgreSQL: healthy'

# -- 3. Backup din sursa -----------------------------------------------------
# Connection string-ul sursei ajunge in container prin mediul procesului
# (`-e DB_CONNECTION_STRING` fara valoare), nu in linia de comanda, unde parola
# ar fi vizibila in lista de procese.
Step 'Backup al bazei sursa (doar baza, fara MinIO)'
$env:DB_CONNECTION_STRING = $Source
try {
    docker compose --profile backup run --rm -e DB_CONNECTION_STRING backup backup --database-only
    if ($LASTEXITCODE -ne 0) { Fail 'Backup-ul sursei a esuat (mesajul de mai sus).' }

    $srcCounts = Parse-Counts (docker compose --profile backup run --rm -e DB_CONNECTION_STRING backup counts)
    if ($LASTEXITCODE -ne 0 -or $srcCounts.Count -eq 0) { Fail 'Nu am putut numara randurile din sursa.' }
} finally {
    Remove-Item Env:DB_CONNECTION_STRING -ErrorAction SilentlyContinue
}

$backup = Get-ChildItem (Join-Path $root 'backups') -Directory |
          Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
          Sort-Object Name | Select-Object -Last 1
if (-not $backup) { Fail 'Nu gasesc backup-ul abia creat in .\backups.' }
Write-Host "Backup: $($backup.Name) ($($srcCounts.Count) tabele)"

# -- 4. Restaurare in PostgreSQL local ---------------------------------------
# Fara -e: containerul foloseste conexiunea implicita, spre serviciul postgres.
Step 'Restaurare in PostgreSQL local'
docker compose --profile backup run --rm backup restore $backup.Name --yes --database-only --skip-policies
if ($LASTEXITCODE -ne 0) { Fail 'Restaurarea a esuat; baza locala a ramas neschimbata (tranzactie anulata).' }

# -- 5. Comparatie -----------------------------------------------------------
Step 'Comparatie: randuri per tabel, sursa vs local'
$dstCounts = Parse-Counts (docker compose --profile backup run --rm backup counts)

$mismatch = 0
$rows = foreach ($t in (@($srcCounts.Keys) + @($dstCounts.Keys) | Sort-Object -Unique)) {
    $s = if ($srcCounts.ContainsKey($t)) { $srcCounts[$t] } else { '-' }
    $d = if ($dstCounts.ContainsKey($t)) { $dstCounts[$t] } else { '-' }
    $ok = ($s -eq $d)
    if (-not $ok) { $mismatch++ }
    [pscustomobject]@{ Tabel = $t; Sursa = $s; Local = $d; Stare = $(if ($ok) { 'OK' } else { 'DIFERIT' }) }
}
$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($mismatch -gt 0) {
    Fail "$mismatch tabele difera. .env NU a fost modificat; aplicatia foloseste in continuare sursa."
}
$total = ($srcCounts.Values | Measure-Object -Sum).Sum
Write-Host "Toate cele $($srcCounts.Count) tabele corespund ($total randuri)." -ForegroundColor Green

# -- 6. .env -----------------------------------------------------------------
if ($KeepEnv) {
    Write-Host ''
    Write-Host '-KeepEnv: .env neschimbat. Pentru a folosi baza locala, puneti in .env:'
    Write-Host "DB_CONNECTION_STRING=Host=localhost;Port=$(if ($cfg['POSTGRES_HOST_PORT']) { $cfg['POSTGRES_HOST_PORT'] } else { '5432' });Database=...;Username=...;Password=<POSTGRES_PASSWORD>"
    exit 0
}

Step 'Actualizez .env'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item $envPath "$envPath.bak-$stamp"

$cfg  = Read-DotEnv
$port = if ($cfg['POSTGRES_HOST_PORT']) { $cfg['POSTGRES_HOST_PORT'] } else { '5432' }
$db   = if ($cfg['POSTGRES_DB'])        { $cfg['POSTGRES_DB'] }        else { 'sgdm' }
$user = if ($cfg['POSTGRES_USER'])      { $cfg['POSTGRES_USER'] }      else { 'sgdm' }
$local = "Host=localhost;Port=$port;Database=$db;Username=$user;Password=$($cfg['POSTGRES_PASSWORD'])"

$text = [IO.File]::ReadAllText($envPath, [Text.Encoding]::UTF8)
if ($text -notmatch '(?m)^SUPABASE_CONNECTION_STRING=') {
    # Pastrata pentru revenire. Nimic din aplicatie nu o citeste.
    $text = $text.TrimEnd() + "`r`n`r`n# Baza veche (Supabase), pastrata pentru revenire - nu e folosita de aplicatie.`r`nSUPABASE_CONNECTION_STRING=$Source`r`n"
}
if ($text -match '(?m)^DB_CONNECTION_STRING=[^\r\n]*') {
    $text = [regex]::Replace($text, '(?m)^DB_CONNECTION_STRING=[^\r\n]*', "DB_CONNECTION_STRING=$local")
} else {
    $text = $text.TrimEnd() + "`r`nDB_CONNECTION_STRING=$local`r`n"
}
[IO.File]::WriteAllText($envPath, $text, (New-Object Text.UTF8Encoding $false))
Write-Host "DB_CONNECTION_STRING -> localhost:$port/$db. Copie a vechiului .env: .env.bak-$stamp"

Step 'Gata'
Write-Host 'Urmatorii pasi:'
Write-Host '  Mod Docker:      docker compose --profile ldap up -d'
Write-Host '  Mod dezvoltare:  dotnet run --project MAI.Api'
Write-Host '  Verificare:      autentificare cu un cont local + un transfer primit care se deschide'
Write-Host '  Migrari si rolul aplicatiei (inainte de API; in Docker ruleaza automat la up):'
Write-Host '                   docker compose run --rm migrate'
Write-Host '                   (local: dotnet run --project MAI.Api -- db:migrate)'
Write-Host ''
Write-Host 'Revenire la Supabase (valoarea e in SUPABASE_CONNECTION_STRING din .env):'
Write-Host '  dotnet run:  copiati-o peste DB_CONNECTION_STRING'
Write-Host '  Docker:      copiati-o in DB_CONTAINER_CONNECTION_STRING (migrate, backup);'
Write-Host '               API-ul foloseste DB_CONTAINER_APP_CONNECTION_STRING (rolul aplicatiei)'
