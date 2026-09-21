# SGDM - certificatul autosemnat al nginx, de incredere pe aceasta masina (Windows).
#
# Doar pentru demonstratie. La prima pornire, serviciul web genereaza un
# certificat autosemnat in .\certs\tls\tls.crt. Browserul il refuza pana cand
# este adaugat in magazinul de incredere al utilizatorului curent. Scriptul:
#   1. importa certificatul in Cert:\CurrentUser\Root (fara drepturi de admin;
#      Windows afiseaza o confirmare - raspundeti Da);
#   2. verifica linia din fisierul hosts pentru SGDM_SERVER_NAME din .env.
#
# De ce e sigur: certificatul are CA:FALSE si serverAuth, deci chiar importat
# ca radacina nu poate semna certificate pentru alte site-uri. Cheia privata
# nu paraseste .\certs\tls.
#
# In productie NU se foloseste: certificatul vine de la CA-ul institutiei, deja
# distribuit pe statii prin politica de grup.
#
# Fisier doar ASCII (fara diacritice, fara BOM): Windows PowerShell 5.1
# citeste un .ps1 fara BOM in codificarea ANSI.
#
# Exemple (din radacina repo-ului):
#   .\scripts\trust-dev-cert.ps1
#   .\scripts\trust-dev-cert.ps1 -AddHostsEntry      (PowerShell ca Administrator)
#   .\scripts\trust-dev-cert.ps1 -Remove

param(
    # Adauga "127.0.0.1 <SGDM_SERVER_NAME>" in fisierul hosts (cere Administrator).
    [switch]$AddHostsEntry,
    # Scoate certificatul din magazinul de incredere.
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$certPath = Join-Path $root 'certs\tls\tls.crt'

# Numele serverului, din .env (acelasi pe care il foloseste nginx).
$serverName = 'sgdm.local'
$envFile = Join-Path $root '.env'
if (Test-Path $envFile) {
    $line = Get-Content $envFile | Where-Object { $_ -match '^\s*SGDM_SERVER_NAME\s*=' } | Select-Object -First 1
    if ($line) { $serverName = ($line -split '=', 2)[1].Trim() }
}

if (-not (Test-Path $certPath)) {
    Write-Host "Nu exista $certPath." -ForegroundColor Yellow
    Write-Host "Porniti intai serviciul web (il genereaza la prima pornire):  docker compose up -d --build"
    exit 1
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certPath
$existing = Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }

if ($Remove) {
    if ($existing) {
        $existing | Remove-Item
        Write-Host "Certificatul $($cert.Thumbprint) a fost scos din magazinul de incredere." -ForegroundColor Green
    } else {
        Write-Host 'Certificatul nu era in magazinul de incredere.'
    }
    exit 0
}

Write-Host "Certificat: $($cert.Subject)"
Write-Host "Valabil pana la: $($cert.NotAfter)"
Write-Host "Amprenta SHA-1 (Windows): $($cert.Thumbprint)"

# Un certificat autosemnat pentru alt nume decat cel din .env ar fi importat
# degeaba: browserul l-ar refuza pentru nepotrivire de nume.
$san = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
if ($san -and ($san.Format($false) -notmatch [regex]::Escape($serverName))) {
    Write-Host "ATENTIE: certificatul nu contine numele '$serverName' din .env." -ForegroundColor Yellow
    Write-Host "Stergeti .\certs\tls\tls.crt si tls.key, apoi:  docker compose restart web"
    exit 1
}

if ($existing) {
    Write-Host 'Certificatul este deja de incredere.' -ForegroundColor Green
} else {
    Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    Write-Host 'Certificat importat in Cert:\CurrentUser\Root.' -ForegroundColor Green
    Write-Host 'Reporniti browserul (Chrome/Edge). Firefox are magazin propriu: about:config -> security.enterprise_roots.enabled = true'
}

# Fisierul hosts: fara el, numele nu se rezolva (nu exista DNS pentru sgdm.local).
$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$pattern = '^\s*127\.0\.0\.1\s+.*\b' + [regex]::Escape($serverName) + '\b'
$hasEntry = (Get-Content $hostsPath) -match $pattern

if ($hasEntry) {
    Write-Host "Fisierul hosts contine deja $serverName." -ForegroundColor Green
} elseif ($AddHostsEntry) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host 'Pentru -AddHostsEntry porniti PowerShell ca Administrator.' -ForegroundColor Yellow
        exit 1
    }
    Add-Content -Path $hostsPath -Value "`r`n127.0.0.1 $serverName"
    Write-Host "Adaugat in hosts: 127.0.0.1 $serverName" -ForegroundColor Green
} else {
    Write-Host "Lipseste din hosts: 127.0.0.1 $serverName" -ForegroundColor Yellow
    Write-Host 'Rulati din nou cu -AddHostsEntry, intr-un PowerShell pornit ca Administrator.'
}

Write-Host ''
Write-Host "Aplicatia: https://$serverName"
