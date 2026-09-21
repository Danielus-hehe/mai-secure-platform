#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
# SGDM - certificatul TLS al nginx
#
# Rulează automat la pornirea containerului (din /docker-entrypoint.d/),
# înaintea generării configurației.
#
#   • Există ./certs/tls/tls.crt și tls.key pe gazdă → se folosesc, neatinse.
#     Acesta e cazul de producție: certificat emis de CA-ul instituției.
#   • Lipsesc ambele și SGDM_SELF_SIGNED=true → se generează un certificat
#     autosemnat pentru SGDM_SERVER_NAME, salvat pe gazdă, reutilizat la
#     repornire (altfel browserul ar cere o nouă excepție după fiecare restart).
#   • Lipsesc și SGDM_SELF_SIGNED=false → containerul NU pornește. Pe un server
#     real, un certificat autosemnat apărut „din senin” învață utilizatorii să
#     accepte avertismentele de securitate - exact ce exploatează un atac MITM.
# ─────────────────────────────────────────────────────────────────────────────
set -eu

TLS_DIR=/etc/nginx/tls
CRT="$TLS_DIR/tls.crt"
KEY="$TLS_DIR/tls.key"
NAME="${SGDM_SERVER_NAME:-localhost}"

if [ -s "$CRT" ] && [ -s "$KEY" ]; then
    echo "[sgdm-tls] Certificat existent: $(openssl x509 -in "$CRT" -noout -subject -enddate | tr '\n' ' ')"

    # Doar avertisment, nu oprire: un certificat expirat trebuie înlocuit, dar
    # un site oprit complet nu ajută pe nimeni să îl înlocuiască mai repede.
    if ! openssl x509 -in "$CRT" -noout -checkend 0 >/dev/null; then
        echo "[sgdm-tls] ATENTIE: certificatul a EXPIRAT. Browserele vor refuza conexiunea." >&2
    elif ! openssl x509 -in "$CRT" -noout -checkend 2592000 >/dev/null; then
        echo "[sgdm-tls] ATENTIE: certificatul expira in mai putin de 30 de zile." >&2
    fi
    exit 0
fi

# Doar unul dintre fișiere: cel mai probabil o copiere incompletă. Generarea
# unui certificat nou ar suprascrie o cheie privată reală.
if [ -e "$CRT" ] || [ -e "$KEY" ]; then
    echo "[sgdm-tls] EROARE: exista doar unul dintre tls.crt / tls.key in ./certs/tls." >&2
    echo "[sgdm-tls] Copiati ambele fisiere sau stergeti-l pe cel ramas." >&2
    exit 1
fi

if [ "${SGDM_SELF_SIGNED:-true}" != "true" ]; then
    echo "[sgdm-tls] EROARE: lipseste certificatul (./certs/tls/tls.crt, tls.key)" >&2
    echo "[sgdm-tls] si SGDM_SELF_SIGNED=false. Montati certificatul institutiei." >&2
    exit 1
fi

echo "[sgdm-tls] Generez certificat AUTOSEMNAT pentru $NAME (doar pentru demonstratie)."
mkdir -p "$TLS_DIR"

# ECDSA P-256: echivalent ca securitate cu RSA-3072, dar semnăturile la
# handshake sunt de zeci de ori mai rapide.
#
# 397 de zile: plafonul acceptat de browsere pentru certificatele de server.
#
# Extensiile contează mai mult decât pare:
#   • subjectAltName - browserele ignoră CN de ani de zile; fără SAN, orice
#     certificat e „invalid pentru acest nume”, chiar importat ca de încredere;
#   • CA:FALSE + serverAuth - certificatul poate fi importat în magazinul de
#     încredere Windows (scripts/trust-dev-cert.ps1) fără să devină o autoritate
#     care ar putea semna certificate pentru ORICE site.
openssl req -x509 -nodes \
    -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
    -days 397 \
    -subj "/CN=$NAME/O=SGDM (autosemnat)" \
    -addext "subjectAltName=DNS:$NAME,DNS:localhost,IP:127.0.0.1" \
    -addext "basicConstraints=critical,CA:FALSE" \
    -addext "keyUsage=critical,digitalSignature" \
    -addext "extendedKeyUsage=serverAuth" \
    -keyout "$KEY" -out "$CRT" 2>/dev/null

chmod 600 "$KEY"
chmod 644 "$CRT"

echo "[sgdm-tls] Amprenta SHA-256: $(openssl x509 -in "$CRT" -noout -fingerprint -sha256 | cut -d= -f2)"
