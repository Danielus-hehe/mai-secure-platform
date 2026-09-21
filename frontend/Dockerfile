# ─────────────────────────────────────────────────────────────────────────────
# SGDM - imaginea web: frontend-ul React servit de nginx, plus reverse proxy
# spre API și terminarea TLS.
#
# Două etape:
#   build   → Node, `npm ci` + `npm run build` (tsc -b strict + vite build)
#   runtime → nginx:alpine, doar fișierele statice din dist/ și configurația
#
# De ce frontend-ul și proxy-ul stau în aceeași imagine: browserul vede o
# singură origine (https://sgdm.local), iar /api/ ajunge la API prin același
# nginx. Consecințele sunt toate în favoarea securității:
#   • CORS nu mai intervine deloc (cereri same-origin);
#   • CSP poate restrânge connect-src la 'self', fără localhost sau porturi;
#   • un singur certificat, un singur loc unde se configurează TLS.
#
# Construire manuală:  docker build -t sgdm-web ./frontend
# Prin compose:        docker compose up -d --build web
# ─────────────────────────────────────────────────────────────────────────────

# ── Etapa 1: build ──────────────────────────────────────────────────────────
# Debian (glibc), nu Alpine (musl): Vite 8 (rolldown) și Tailwind 4 (oxide)
# vin cu binare native alese după platformă, ca dependențe opționale. Un
# package-lock.json generat pe Windows nu conține întotdeauna varianta musl, iar
# `npm ci` pe Alpine eșuează la build. glibc e și ce folosește jobul frontend
# din CI, deci imaginea se construiește exact în condițiile deja testate.
FROM node:22-bookworm-slim AS build
WORKDIR /app

# Întâi doar manifestele: `npm ci` intră într-un strat de cache separat și se
# repetă doar când se schimbă dependențele, nu la fiecare modificare de cod.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .

# Adresa API-ului se „coace” în bundle la build (Vite înlocuiește
# import.meta.env.VITE_API_URL cu un șir literal). O cale relativă face
# imaginea independentă de numele DNS: aceeași imagine merge pe sgdm.local,
# pe un IP sau pe domeniul real, fiindcă /api e rezolvat față de pagina curentă.
# Valoarea din .env (http://localhost:5000/api) e pentru `npm run dev` și NU
# ajunge aici: .env nu e în contextul de build (vezi frontend/.dockerignore).
ARG VITE_API_URL=/api
ENV VITE_API_URL=${VITE_API_URL}

RUN npm run build

# ── Etapa 2: runtime ────────────────────────────────────────────────────────
FROM nginx:1.27-alpine AS runtime

# openssl doar pentru certificatul autosemnat generat la prima pornire
# (15-sgdm-tls.sh). Cu un certificat instituțional montat, nu mai e folosit.
RUN apk add --no-cache openssl \
 && rm -f /etc/nginx/conf.d/default.conf

# Șablonul e procesat de scriptul oficial 20-envsubst-on-templates.sh, care
# scrie rezultatul în /etc/nginx/conf.d/default.conf la fiecare pornire.
COPY nginx/default.conf.template  /etc/nginx/templates/default.conf.template
COPY nginx/security-headers.conf  /etc/nginx/snippets/security-headers.conf
COPY nginx/15-sgdm-tls.sh         /docker-entrypoint.d/15-sgdm-tls.sh

# Pe Windows, Git poate aduce fișierele cu CRLF (core.autocrlf=true). Un script
# shell cu CRLF eșuează în container cu „not found”, deși fișierul există.
# .gitattributes forțează LF, iar linia de mai jos acoperă o copie făcută pe
# lângă Git (arhivă dezarhivată, editor care a salvat CRLF).
RUN sed -i 's/\r$//' /docker-entrypoint.d/15-sgdm-tls.sh \
        /etc/nginx/templates/default.conf.template \
        /etc/nginx/snippets/security-headers.conf \
 && chmod +x /docker-entrypoint.d/15-sgdm-tls.sh

COPY --from=build /app/dist /usr/share/nginx/html

# Valori implicite; docker-compose.yml le suprascrie din .env.
#
# NGINX_ENVSUBST_FILTER: fără el, envsubst ar înlocui ORICE variabilă de mediu
# definită care apare în șablon. Containerul are mereu HOSTNAME, PATH, HOME;
# o variabilă nginx scrisă din greșeală cu majuscule ar fi înlocuită tacit.
# Cu filtrul, se ating doar variabilele SGDM_*.
ENV SGDM_SERVER_NAME=localhost \
    SGDM_PUBLIC_ORIGIN=https://localhost \
    SGDM_API_UPSTREAM=api:8080 \
    SGDM_DNS_RESOLVER=127.0.0.11 \
    SGDM_SELF_SIGNED=true \
    NGINX_ENVSUBST_FILTER=^SGDM_

EXPOSE 80 443
