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
FROM postgres:${PG_MAJOR}-alpine

# mc se copiază din imaginea oficială (binar Go static, multi-arhitectură),
# nu se descarcă din internet la build: fără URL-uri pe care nu le verifică
# nimeni și fără diferențe între amd64 și arm64.
COPY --from=minio/mc:latest /usr/bin/mc /usr/local/bin/mc

RUN apk add --no-cache gnupg coreutils

COPY sgdm-backup.sh /usr/local/bin/sgdm-backup
RUN sed -i 's/\r$//' /usr/local/bin/sgdm-backup \
 && chmod +x /usr/local/bin/sgdm-backup

# Imaginea postgres rulează implicit serverul; aici nu vrem niciun server.
ENTRYPOINT ["sgdm-backup"]
CMD ["backup"]
