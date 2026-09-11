# ─────────────────────────────────────────────────────────────────────────────
# SGDM — imaginea API-ului (.NET 8)
#
# Două etape:
#   build   → SDK-ul complet, restaurează pachetele și publică în Release
#   runtime → doar runtime-ul ASP.NET, fără compilator și fără cod sursă
#
# Imaginea NU conține niciun secret. Cheia JWT, pepper-ul Argon2, cheia 2FA,
# conexiunea la bază și credențialele MinIO vin la pornire, din variabile de
# mediu (vezi docker-compose.yml și .env.example). Fișierele de configurare cu
# valori reale (appsettings.Development.json, .env) sunt excluse din contextul
# de build prin .dockerignore.
#
# Construire manuală:   docker build -t sgdm-api .
# Prin compose:         docker compose up -d --build api
# ─────────────────────────────────────────────────────────────────────────────

# ── Etapa 1: build ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Întâi doar fișierele de proiect: restore-ul intră într-un strat separat de
# cache și nu se repetă la fiecare modificare de cod, doar când se schimbă
# pachetele.
COPY MAI.Domain/MAI.Domain.csproj                   MAI.Domain/
COPY MAI.DataAccessLayer/MAI.DataAccessLayer.csproj MAI.DataAccessLayer/
COPY MAI.BusinessLogic/MAI.BusinessLogic.csproj     MAI.BusinessLogic/
COPY MAI.Api/MAI.Api.csproj                         MAI.Api/
RUN dotnet restore MAI.Api/MAI.Api.csproj

COPY MAI.Domain/          MAI.Domain/
COPY MAI.DataAccessLayer/ MAI.DataAccessLayer/
COPY MAI.BusinessLogic/   MAI.BusinessLogic/
COPY MAI.Api/             MAI.Api/

RUN dotnet publish MAI.Api/MAI.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# ── Etapa 2: runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl e folosit doar de healthcheck-ul din docker-compose.yml. Imaginea
# aspnet nu îl include implicit.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Directoarele scrise la rulare: Storage/ (provider Local) și uploads/
# (versiunile de documente anterioare mutării în MinIO). Se creează aici, cu
# proprietarul corect, ca volumele Docker montate peste ele să moștenească
# drepturile utilizatorului neprivilegiat.
RUN mkdir -p /app/Storage /app/uploads \
 && chown -R app:app /app/Storage /app/uploads

# Utilizatorul „app” vine cu imaginile .NET 8. Procesul nu rulează ca root:
# o vulnerabilitate în aplicație nu primește automat controlul containerului.
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true

EXPOSE 8080

ENTRYPOINT ["dotnet", "MAI.Api.dll"]
