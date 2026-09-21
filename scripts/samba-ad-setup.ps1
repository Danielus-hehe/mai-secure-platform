# =============================================================================
# SGDM - pregatirea domeniului Samba AD de laborator
#
# Ruleaza DUPA:  docker compose --profile ldap up -d samba-ad
#
# Ce face:
#   1. creeaza unitatile organizatorice, grupurile si conturile de test;
#   2. scoate certificatul CA generat de Samba, pentru ca API-ul sa poata
#      valida LDAPS fara sa accepte orice certificat;
#   3. afiseaza numele si amprenta certificatului, ca sa verificati ca numele
#      corespunde cu LDAP_HOST (dc.sgdm.local).
#
# Nimic din ce e aici nu are legatura cu productia: acolo controlerul de
# domeniu e un server separat, administrat de altcineva, iar noi doar citim
# din el.
#
# Utilizare:
#   .\scripts\samba-ad-setup.ps1
#   .\scripts\samba-ad-setup.ps1 -Password 'Parola-Utilizatorilor1'
# =============================================================================

[CmdletBinding()]
param(
    [string] $Container = 'sgdm-samba-ad',
    [string] $BaseDn    = 'DC=sgdm,DC=local',
    [string] $Password  = 'Parola-Test1',
    [string] $CertDir   = './certs'
)

$ErrorActionPreference = 'Stop'

function Invoke-Samba([string] $Command) {
    docker exec $Container bash -lc $Command
    if ($LASTEXITCODE -ne 0) { throw "Comanda a esuat in container: $Command" }
}

Write-Host '== 1. Unitati organizatorice ==' -ForegroundColor Cyan

# Ierarhia oglindeste structura din aplicatie: Directie > Sectie > Serviciu.
# Adancimea din AD devine nivelul subdiviziunii la import.
Invoke-Samba "samba-tool ou create 'OU=DTI,$BaseDn' || true"
Invoke-Samba "samba-tool ou create 'OU=Retele,OU=DTI,$BaseDn' || true"
Invoke-Samba "samba-tool ou create 'OU=Securitate,OU=DTI,$BaseDn' || true"
Invoke-Samba "samba-tool ou create 'OU=DGP,$BaseDn' || true"

Write-Host '== 2. Grupuri (din ele vin rolurile) ==' -ForegroundColor Cyan

Invoke-Samba "samba-tool group add SGDM-Admins || true"
Invoke-Samba "samba-tool group add SGDM-Sefi || true"
Invoke-Samba "samba-tool group add SGDM-Utilizatori || true"

Write-Host '== 3. Cont de serviciu (doar citire) ==' -ForegroundColor Cyan

# Contul cu care API-ul cauta utilizatorii. Nu primeste niciun drept de
# scriere: aplicatia nu modifica nimic in domeniu.
Invoke-Samba "samba-tool user create svc-sgdm '$Password' --description='Cont de serviciu SGDM (doar citire)' || true"

Write-Host '== 4. Conturi de test ==' -ForegroundColor Cyan

Invoke-Samba "samba-tool user create ion.popescu '$Password' --given-name=Ion --surname=Popescu --userou='OU=Retele,OU=DTI' --department='Secția Rețele' || true"
Invoke-Samba "samba-tool user create maria.rusu '$Password' --given-name=Maria --surname=Rusu --userou='OU=Securitate,OU=DTI' --department='Secția Securitate' || true"
Invoke-Samba "samba-tool user create admin.sgdm '$Password' --given-name=Administrator --surname=SGDM --userou='OU=DTI' --department='Direcția Tehnologii Informaționale' || true"

Invoke-Samba "samba-tool group addmembers SGDM-Admins admin.sgdm || true"
Invoke-Samba "samba-tool group addmembers SGDM-Sefi maria.rusu || true"
Invoke-Samba "samba-tool group addmembers SGDM-Utilizatori ion.popescu || true"

Write-Host '== 5. Certificatul CA pentru LDAPS ==' -ForegroundColor Cyan

# Samba isi genereaza propriul CA la provizionare. Fara el, API-ul ar trebui
# sa accepte orice certificat - adica sa renunte la singura protectie care
# impiedica pe cineva sa se dea drept controler de domeniu si sa colecteze
# parolele.
if (-not (Test-Path $CertDir)) { New-Item -ItemType Directory -Path $CertDir | Out-Null }

docker cp "${Container}:/var/lib/samba/private/tls/ca.pem" "$CertDir/ad-ca.pem"
if ($LASTEXITCODE -ne 0) { throw 'Certificatul CA nu a putut fi copiat din container.' }

Write-Host "CA salvat in $CertDir/ad-ca.pem" -ForegroundColor Green

Write-Host '== 6. Verificarea numelui din certificat ==' -ForegroundColor Cyan

# OpenLDAP (folosit de API in container) compara numele din certificat cu
# LDAP_HOST. Daca aici nu apare dc.sgdm.local, conexiunea va fi refuzata chiar
# si cu CA-ul corect.
docker exec $Container bash -lc "openssl x509 -in /var/lib/samba/private/tls/cert.pem -noout -subject -fingerprint -sha256"

Write-Host ''
Write-Host 'Puneti in .env:' -ForegroundColor Cyan
Write-Host '  LDAP_ENABLED=true'
Write-Host '  LDAP_CA_FILE=/app/certs/ad-ca.pem'
Write-Host "  MAI_LDAP_BIND_PASSWORD=$Password"
Write-Host ''
Write-Host 'Apoi:  docker compose up -d --build api' -ForegroundColor Cyan
