# SGDM - recriptarea documentelor din depozit (documents/, internal/).
#
# Invelitoare peste comanda API-ului "storage:recrypt". Foloseste aceeasi
# configurare (.env) ca aplicatia. Detalii: docs/STORAGE-ENCRYPTION.md
#
# Fisierul este intentionat doar ASCII (fara diacritice, fara BOM): Windows
# PowerShell 5.1 citeste un .ps1 fara BOM in codificarea ANSI, iar orice
# caracter non-ASCII strica interpretarea scriptului.
#
# Exemple (din radacina repo-ului):
#   .\scripts\storage-recrypt.ps1 -DryRun
#   .\scripts\storage-recrypt.ps1
#   .\scripts\storage-recrypt.ps1 -Docker -DryRun

param(
    # Doar raporteaza ce s-ar face; nu scrie nimic in depozit.
    [switch]$DryRun,
    # Ruleaza in containerul api (docker compose run), nu cu dotnet run.
    [switch]$Docker
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $toolArgs = @('storage:recrypt')
    if ($DryRun) { $toolArgs += '--dry-run' }

    if ($Docker) {
        docker compose run --rm api @toolArgs
    } else {
        dotnet run --project MAI.Api -- @toolArgs
    }
    $code = $LASTEXITCODE

    if ($code -eq 0) {
        Write-Host 'Recriptare terminata fara probleme.' -ForegroundColor Green
    } elseif ($code -eq 1) {
        Write-Host 'Unele obiecte au probleme - vedeti lista de mai sus. Ele au ramas neatinse.' -ForegroundColor Yellow
    } else {
        Write-Host "Oprit cu codul $code (configurare incompleta sau eroare la pornire)." -ForegroundColor Red
    }
    exit $code
}
finally {
    Pop-Location
}
