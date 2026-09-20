\xef\xbb\xbf<#
.SYNOPSIS
    Criptează documentele vechi din depozit și le mută pe cheia principală activă.

.DESCRIPTION
    Învelitoare peste comanda API-ului "storage:recrypt". Folosește aceeași
    configurare (.env) ca aplicația. Vezi docs/STORAGE-ENCRYPTION.md.

.EXAMPLE
    .\scripts\storage-recrypt.ps1 -DryRun
    .\scripts\storage-recrypt.ps1
    .\scripts\storage-recrypt.ps1 -Docker -DryRun
#>
param(
    # Doar raportează ce s-ar face; nu scrie nimic în depozit.
    [switch]$DryRun,
    # Rulează în containerul api (docker compose run), nu cu dotnet run.
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

    switch ($LASTEXITCODE) {
        0 { Write-Host 'Recriptare terminata fara probleme.' -ForegroundColor Green }
        1 { Write-Host 'Unele obiecte au probleme - vedeti lista de mai sus. Ele au ramas neatinse.' -ForegroundColor Yellow }
        default { Write-Host "Configurare incompleta (cod $LASTEXITCODE)." -ForegroundColor Red }
    }
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
