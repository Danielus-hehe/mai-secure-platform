# ─────────────────────────────────────────────────────────────────────────────
# SGDM - imaginea de backup / restaurare
#
# Nu rulează permanent: o pornește la cerere comanda
#   docker compose --profile backup run --rm backup
# (manual, din Task Scheduler pe Windows sau din cron pe Linux).
#
# Conține exact trei unelte: pg_dump/pg_restore (baza), mc (MinIO) și gpg
# (cifrarea opțională a dump-ului). Aceleași comenzi pe Windows și pe Linux,
# fără să instalezi PostgreSQL sau MinIO Client pe gazdă.
# ─────────────────────────────────────────────────────────────────────────────

# pg_dump trebuie să fie de aceeași versiune majoră cu serverul sau mai nou:
# un pg_dump 15 refuză să citească un server 17. Supabase rulează 15 sau 17,
# un Postgres local din compose 16; 17 le acoperă pe toate. Pentru un server
# mai nou: docker compose build --build-arg PG_MAJOR=18 backup
ARG PG_MAJOR=17

# ── mc, compilat din sursă ───────────────────────────────────────────────────
# MinIO nu mai publică imagini pentru comunitate: minio/mc a dispărut de pe
# Docker Hub (11.09.2026), iar quay.io/minio/mc răspunde 401 la orice pull
# anonim (24.09.2026). Nu mai există un registru oficial din care să copiem
# binarul, deci îl compilăm din codul sursă publicat.
#
#   - fixat pe commit, nu pe etichetă: tag-ul RELEASE.2025-08-13T08-35-41Z
#     poate fi mutat, un commit nu. Același commit îl folosește și Wolfi
#     (Chainguard) pentru pachetul mc;
#   - CGO_ENABLED=0: binar static. Imaginea finală e Alpine (musl); un binar
#     legat dinamic de glibc (ca cel din imaginile Chainguard) nu ar porni;
#   - prin proxy-ul de module Go, care păstrează definitiv orice versiune
#     publicată și verifică sumele de control (sum.golang.org). Dispariția
#     depozitului de pe GitHub nu mai poate strica build-ul.
FROM golang:1.24-alpine AS mc
ARG MC_COMMIT=7394ce0dd2a80935aded936b09fa12cbb3cb8096
RUN CGO_ENABLED=0 GOBIN=/out go install -trimpath -ldflags "-s -w" "github.com/minio/mc@${MC_COMMIT}"

FROM postgres:${PG_MAJOR}-alpine

COPY --from=mc /out/mc /usr/local/bin/mc

RUN apk add --no-cache gnupg coreutils

COPY sgdm-backup.sh /usr/local/bin/sgdm-backup
RUN sed -i 's/\r$//' /usr/local/bin/sgdm-backup \
 && chmod +x /usr/local/bin/sgdm-backup

# Imaginea postgres rulează implicit serverul; aici nu vrem niciun server.
ENTRYPOINT ["sgdm-backup"]
CMD ["backup"]
