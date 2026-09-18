using MAI.Api.BackgroundJobs;
using MAI.Api.Configuration;
using MAI.Api.Middleware;
using MAI.Api.Options;
using MAI.Api.Security;
using MAI.Api.Services;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.BusinessLogic.Services;
using MAI.BusinessLogic.Storage;
using MAI.DataAccessLayer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;

// ─── Logger de pornire ─────────────────────────────────────────────────────
// Există înainte ca host-ul să fie construit, ca erorile de configurare (cheie
// JWT lipsă, pepper-ul rămas pe valoarea-șablon, AllowLegacyPlaintext în
// producție) să apară și ele ca JSON, nu ca stack trace în text liber pe care
// colectorul de loguri nu îl poate parsa. UseSerilog de mai jos îl înlocuiește.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "SGDM.Api")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    // ─── .env local (o singură sursă de configurare cu Docker Compose) ─────────
    // Rulat înainte de CreateBuilder: provider-ul de variabile de mediu își face
    // instantaneul la construire. În container nu face nimic (vezi DotEnvLoader).
    var dotEnv = DotEnvLoader.LoadFromRepositoryRoot();
    if (dotEnv.Path is not null)
    {
        // Doar numele cheilor, niciodată valorile.
        Log.Information(
            "Configurare locală încărcată din {DotEnvPath}: {Count} chei aplicate ({Keys})",
            dotEnv.Path, dotEnv.Applied, string.Join(", ", dotEnv.Keys));
    }

    var builder = WebApplication.CreateBuilder(args);

    // ─── Loguri structurate (Serilog, JSON pe consolă) ─────────────────────────
    // Un eveniment = o linie JSON, cu proprietățile separate de mesaj:
    //   {"@t":"...","@m":"Rate limit depasit: IP=10.0.0.7 ...","@l":"Warning","Ip":"10.0.0.7",...}
    // Asta permite `docker logs sgdm-api | jq 'select(.StatusCode == 429)'` sau
    // colectarea directă în Loki/ELK, fără expresii regulate peste text liber.
    //
    // Nivelurile vin din secțiunea "Serilog" din appsettings; sink-ul și formatul
    // sunt fixate aici, intenționat: formatul de ieșire e un contract cu cine
    // colectează logurile, nu o preferință de configurare per mediu.
    //
    // Logurile NU înlocuiesc jurnalul de audit. Auditul e în baza de date, e
    // vizibil în aplicație și răspunde la „cine a făcut ce”; logurile răspund la
    // „ce s-a întâmplat cu procesul” și se rotesc. Nicio decizie de securitate
    // nu depinde de ele.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "SGDM.Api")
        .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

    var isDevelopment = builder.Environment.IsDevelopment();

    // ─── Secrete rămase pe valoarea-șablon ─────────────────────────────────────
    // Fișierele versionate (appsettings.json, .env.example, *.example) conțin
    // valori-șablon. Un secret rămas pe ele e public: îl știe oricine vede
    // repository-ul.
    //
    // Regula e aceeași pentru toate secretele:
    //   • în afara mediului Development → aplicația refuză să pornească;
    //   • în Development → pornește, dar scrie un avertisment în log.
    //
    // Toleranța din Development e deliberată. Schimbarea pepper-ului invalidează
    // toate parolele, iar schimbarea cheii JWT schimbă și cheia 2FA derivată din
    // ea. Un mediu local existent nu trebuie să se strice la prima pornire după
    // această verificare. Exact aceeași configurație în Docker (Production) nu
    // mai pornește.
    void EnsureNotTemplate(string setting, string? value, string howToFix)
    {
        if (!PlaceholderSecrets.IsPlaceholder(value)) return;

        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                $"{setting} are încă valoarea-șablon din repository, deci este publică. {howToFix}");
        }

        Log.Warning(
            "{Setting} are valoarea-șablon din repository. Acceptat DOAR în Development; " +
            "în orice alt mediu aplicația refuză să pornească. {HowToFix}",
            setting, howToFix);
    }

    builder.Services.AddControllers(options =>
    {
        // 2FA obligatoriu pentru operațiile privilegiate, dacă
        // TwoFactor:RequiredForPrivilegedRoles = true. Filtrul se aplică doar
        // endpointurilor care cer explicit un rol; restul aplicației, inclusiv
        // înrolarea 2FA din profil, rămâne accesibil. Vezi PrivilegedMfaFilter.
        options.Filters.Add<PrivilegedMfaFilter>();
    });
    builder.Services.AddEndpointsApiExplorer();

    // ─── Swagger complet configurat ────────────────────────────────────────────
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title       = "MAI SGDM API",
            Version     = "v1",
            Description = "Sistem de Gestiune Documente și Transferuri Securizate",
        });

        options.MapType<Guid>(() => new OpenApiSchema
        {
            Type    = "string",
            Format  = "uuid",
            Example = new Microsoft.OpenApi.Any.OpenApiString("00000000-0000-0000-0000-000000000000"),
        });

        options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
        options.CustomSchemaIds(type => type.FullName?.Replace("+", "_") ?? type.Name);

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name         = "Authorization",
            Type         = SecuritySchemeType.Http,
            Scheme       = "bearer",
            BearerFormat = "JWT",
            In           = ParameterLocation.Header,
            Description  = "Introduceți tokenul JWT. Exemplu: Bearer eyJ...",
        });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id   = "Bearer",
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    // ─── Stocarea fișierelor ───────────────────────────────────────────────────
    // Provider configurabil: MinIO/S3 în producție, filesystem local în dezvoltare.
    // Restul aplicației vede doar IFileStorage și nu știe care rulează.

    var storageOptions = new StorageOptions();
    builder.Configuration.GetSection("Storage").Bind(storageOptions);

    // Credențialele preferabil din variabile de mediu, nu din fișierul de configurare
    // care ajunge în Git.
    var storageAccessKey = Environment.GetEnvironmentVariable("MAI_STORAGE_ACCESS_KEY");
    var storageSecretKey = Environment.GetEnvironmentVariable("MAI_STORAGE_SECRET_KEY");
    if (!string.IsNullOrWhiteSpace(storageAccessKey)) storageOptions.AccessKey = storageAccessKey;
    if (!string.IsNullOrWhiteSpace(storageSecretKey)) storageOptions.SecretKey = storageSecretKey;

    storageOptions.Validate();

    if (storageOptions.IsS3)
    {
        EnsureNotTemplate(
            "Storage:SecretKey (MAI_STORAGE_SECRET_KEY)",
            storageOptions.SecretKey,
            "Setați MINIO_ROOT_PASSWORD în .env la o parolă generată (minim 8 caractere).");
    }

    builder.Services.AddSingleton(storageOptions);

    if (storageOptions.IsS3)
        builder.Services.AddSingleton<IFileStorage, S3FileStorage>();
    else
        builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

    // ─── Kestrel și multipart — limitele corpului cererii ──────────────────────
    // Limita GLOBALĂ e mică: toate endpointurile JSON (login, chei, 2FA,
    // administrare) primesc câțiva kiloocteți. Înainte era ridicată global la
    // 51 MB pentru upload, deci și /api/Auth/login, anonim, accepta 51 MB de
    // JSON pe cerere.
    //
    // Doar endpointurile de upload ridică limita, prin
    // [RequestSizeLimit(UploadLimits.MaxRequestBytes)]. Valoarea din atribut
    // trebuie ținută sincron cu Storage:MaxFileSizeMb; verificarea exactă a
    // dimensiunii fișierului se face oricum în controller.

    builder.Services.Configure<KestrelServerOptions>(options =>
    {
        options.Limits.MaxRequestBodySize = UploadLimits.MaxJsonRequestBytes;
    });

    builder.Services.Configure<FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = storageOptions.MaxFileSizeBytes + 1_048_576;

        // Peste acest prag, ASP.NET bufferizează corpul pe disc, nu în memorie.
        // Valoarea implicită (64 KB) e deja bună; o fixăm explicit ca să nu se
        // schimbe pe tăcute la un upgrade.
        options.MemoryBufferThreshold = 65_536;
    });

    // ─── Argon2id: profile de cost + limită de concurență ─────────────────────
    var argon2Options = new Argon2Options();
    builder.Configuration.GetSection("Argon2").Bind(argon2Options);

    // Pepper-ul e preferabil din variabilă de mediu, nu din fișier de configurare.
    var pepperFromEnv = Environment.GetEnvironmentVariable("MAI_ARGON2_PEPPER");
    if (!string.IsNullOrWhiteSpace(pepperFromEnv))
        argon2Options.Pepper = pepperFromEnv;
    if (argon2Options.Pepper == "YOUR_ARGON2_PEPPER_HERE")
        argon2Options.Pepper = null;

    argon2Options.Validate();

    // Fără pepper, o copie a bazei de date ajunge ca să se încerce parole offline.
    // Validarea de mai sus nu îl cerea: aplicația pornea și hash-uia fără el, deși
    // documentația spunea că pepper-ul lipsă oprește pornirea.
    if (string.IsNullOrWhiteSpace(argon2Options.Pepper))
    {
        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "MAI_ARGON2_PEPPER lipsește. Generați: openssl rand -base64 32. " +
                "Setați-l ÎNAINTE de crearea conturilor: schimbarea ulterioară a " +
                "pepper-ului invalidează toate parolele existente.");
        }

        Log.Warning("Argon2: pepper-ul lipsește. Acceptat DOAR în Development.");
    }

    EnsureNotTemplate(
        "Argon2:Pepper (MAI_ARGON2_PEPPER)",
        argon2Options.Pepper,
        "Generați: openssl rand -base64 32. Atenție: schimbarea pepper-ului invalidează parolele existente.");

    // Acceptarea parolelor în clar are sens doar cât timp mai există conturi
    // nemigrate, adică în dezvoltare. În producție, lăsată pe true, transformă o
    // scurgere a bazei de date într-o listă de parole utilizabile direct.
    if (!builder.Environment.IsDevelopment() && argon2Options.AllowLegacyPlaintext)
    {
        throw new InvalidOperationException(
            "Argon2:AllowLegacyPlaintext=true nu este permis în afara dezvoltării. " +
            "Migrați conturile rămase (login o dată cu fiecare cont) și puneți valoarea pe false.");
    }

    builder.Services.AddSingleton(argon2Options);
    builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

    // ─── Politica de parole ────────────────────────────────────────────────────
    var passwordPolicyOptions = new PasswordPolicyOptions();
    builder.Configuration.GetSection("PasswordPolicy").Bind(passwordPolicyOptions);
    builder.Services.AddSingleton(passwordPolicyOptions);
    builder.Services.AddSingleton<PasswordPolicy>();

    // ─── Autentificare în doi pași (TOTP) — opțională, per utilizator ──────────
    // Nimic nu se activează global. Fiecare utilizator decide din pagina de profil;
    // un cont fără 2FA se autentifică exact ca înainte.

    var twoFactorOptions = new TwoFactorOptions();
    builder.Configuration.GetSection("TwoFactor").Bind(twoFactorOptions);

    // Cheia de cifrare a secretelor TOTP vine din mediu, nu din fișierul care ajunge
    // în Git. Fără ea, în dezvoltare, derivăm una din cheia JWT ca aplicația să
    // pornească — dar NU în producție: o cheie derivată dintr-un secret partajat
    // înseamnă că scurgerea unuia le compromite pe amândouă.
    var twoFactorKeyFromEnv = Environment.GetEnvironmentVariable("MAI_TWOFACTOR_KEY");
    if (!string.IsNullOrWhiteSpace(twoFactorKeyFromEnv))
        twoFactorOptions.EncryptionKey = twoFactorKeyFromEnv;

    if (string.IsNullOrWhiteSpace(twoFactorOptions.EncryptionKey))
    {
        if (builder.Environment.IsDevelopment())
            twoFactorOptions.EncryptionKey = builder.Configuration["Jwt:Key"] + ":2fa";
        else
            throw new InvalidOperationException(
                "MAI_TWOFACTOR_KEY lipsește. Generați: openssl rand -base64 32");
    }

    EnsureNotTemplate(
        "TwoFactor:EncryptionKey (MAI_TWOFACTOR_KEY)",
        twoFactorOptions.EncryptionKey,
        "Generați: openssl rand -base64 32.");

    twoFactorOptions.Validate();

    builder.Services.AddSingleton(twoFactorOptions);
    builder.Services.AddSingleton<TotpService>();
    builder.Services.AddSingleton<SecretProtector>();

    // ─── Blocare cont după încercări eșuate ────────────────────────────────────
    var lockoutOptions = new LockoutOptions();
    builder.Configuration.GetSection("Lockout").Bind(lockoutOptions);
    builder.Services.AddSingleton(lockoutOptions);

    // ─── Rate limiting per IP ──────────────────────────────────────────────────
    var rateLimitOptions = new RateLimitOptions();
    builder.Configuration.GetSection("RateLimit").Bind(rateLimitOptions);
    builder.Services.AddMaiRateLimiting(rateLimitOptions);

    // Fără asta, în spatele unui reverse proxy toți utilizatorii ajung în aceeași
    // găleată de rate limit, pentru că RemoteIpAddress e IP-ul proxy-ului.
    if (rateLimitOptions.BehindReverseProxy)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in rateLimitOptions.KnownProxies)
                if (IPAddress.TryParse(proxy, out var ip))
                    options.KnownProxies.Add(ip);
        });
    }

    // ─── JWT Authentication ────────────────────────────────────────────────────
    // Parametrii se citesc o singură dată, într-un obiect tipizat, folosit ȘI la
    // validare (aici) ȘI la emitere (TokenService). Înainte erau citiți din
    // IConfiguration în două locuri — două locuri care trebuie să rămână identice
    // sunt un loc unde diverg, iar aici divergența înseamnă tokenuri emise pe care
    // serverul propriu le respinge.

    var jwtOptions = new JwtOptions();
    builder.Configuration.GetSection("Jwt").Bind(jwtOptions);

    // Cheia preferabil din variabilă de mediu, nu din fișierul care ajunge în Git.
    var jwtKeyFromEnv = Environment.GetEnvironmentVariable("MAI_JWT_KEY");
    if (!string.IsNullOrWhiteSpace(jwtKeyFromEnv))
        jwtOptions.Key = jwtKeyFromEnv;

    // Oprește pornirea dacă tokenurile ar fi falsificabile sau dacă issuer/audience
    // lipsesc — un server care rulează cu o cheie slabă e mai rău decât unul care
    // nu pornește, fiindcă primul pare că funcționează.
    EnsureNotTemplate(
        "Jwt:Key (MAI_JWT_KEY)",
        jwtOptions.Key,
        "Generați: openssl rand -base64 48.");

    jwtOptions.Validate(allowTemplateKey: isDevelopment);

    builder.Services.AddSingleton(jwtOptions);

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),

                // Înainte erau amândouă false. Consecința: un token emis de ORICE alt
                // serviciu semnat cu aceeași cheie — inclusiv unul dintr-un proiect
                // unde cheia s-a scurs sau a fost refolosită din comoditate — era
                // acceptat aici ca sesiune validă. În intranet riscul e mic, dar
                // validarea costă o comparație de șiruri.
                ValidateIssuer   = true,
                ValidIssuer      = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience    = jwtOptions.Audience,

                ValidateLifetime = true,

                // Fără toleranță de ceas: expirarea din token e expirarea reală.
                // Implicit .NET acordă 5 minute, ceea ce prelungește tăcut orice
                // token de acces cu o treime din durata lui.
                ClockSkew        = TimeSpan.Zero,
            };
        });

    // ─── Servicii de autentificare ─────────────────────────────────────────────
    // Extrase din AuthController, care ajunsese să facă simultan verificarea parolei,
    // pasul doi, blocarea contului, semnarea JWT-urilor și hashingul tokenurilor.
    // Singleton: niciunul nu ține stare per cerere.
    builder.Services.AddSingleton<ITokenService, TokenService>();
    builder.Services.AddSingleton<IAccountLockoutService, AccountLockoutService>();

    // Scoped, spre deosebire de celelalte două: are nevoie de AppDbContext, care e
    // per cerere. Tocmai de aceea emiterea tokenurilor a rămas separată de sesiuni —
    // un serviciu care nu face decât HMAC și numere aleatorii nu trebuie să devină
    // dependent de EF Core și nici să-și piardă durata de viață de singleton.
    builder.Services.AddScoped<ISessionService, SessionService>();

    // ─── CORS ──────────────────────────────────────────────────────────────────
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:5173", "http://localhost:3000"];

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowFrontend", policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("Retry-After");   // frontend-ul îl citește la 429/503

            // AllowCredentials() a fost eliminat intenționat.
            //
            // Tokenul de acces circulă prin antetul Authorization, pus explicit de
            // client.ts — niciodată printr-un cookie. Fără cookie nu există CSRF
            // clasic: browserul nu atașează nimic automat la o cerere cross-origin.
            //
            // AllowCredentials nu era folosit de nimic, dar lăsa ușa deschisă: în ziua
            // în care cineva ar muta tokenul într-un cookie „ca să supraviețuiască
            // refresh-ului”, întreg API-ul ar deveni vulnerabil la CSRF fără ca vreo
            // linie din politica de CORS să se schimbe.
            //
            // Dacă vreodată chiar e nevoie de cookie-uri, se repune AllowCredentials
            // ÎMPREUNĂ cu SameSite=Strict și un token anti-CSRF — nu separat.
        });
    });

    // ─── Database ──────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    // „YOUR_DATABASE_CONNECTION_STRING_HERE” nu poate funcționa în niciun mediu.
    // Fără verificarea asta, eroarea apărea abia la prima interogare, ca un
    // mesaj Npgsql despre formatul șirului, fără legătură vizibilă cu cauza.
    if (string.IsNullOrWhiteSpace(connectionString) ||
        connectionString.StartsWith("YOUR_", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:DefaultConnection lipsește. Setați-l în " +
            "appsettings.Development.json sau prin ConnectionStrings__DefaultConnection.");
    }

    EnsureNotTemplate(
        "ConnectionStrings:DefaultConnection",
        connectionString,
        "Parola bazei de date e încă „SCHIMBA_MA”. Setați DB_CONNECTION_STRING în .env.");

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

    // ─── Job de expirare a transferurilor ──────────────────────────────────────
    // Fără el, "Expirat" era doar o etichetă calculată la afișare: coloana Status
    // rămânea Pending, iar cifrotextul stătea în bucket până la lifecycle policy.
    // Interfața promitea o garanție pe care codul nu o dădea.
    //
    // Se înregistrează ca singleton ȘI ca hosted service, aceeași instanță: astfel
    // StatsController îl poate injecta direct pentru butonul "Rulează acum", fără
    // să scotocească prin lista de IHostedService.

    // ─── Restricție de acces la rețeaua internă ────────────────────────────────
    // Implicit DEZACTIVATĂ. Se activează în appsettings.Production.json, după ce ai
    // confirmat plajele reale — un API care refuză toată lumea la deploy e mai rău
    // decât unul deschis în laborator. Vezi Intranet:AuditOnly pentru rodaj.

    var intranetOptions = new IntranetOptions();
    builder.Configuration.GetSection("Intranet").Bind(intranetOptions);
    intranetOptions.Validate();
    builder.Services.AddSingleton(intranetOptions);

    var expirationOptions = new TransferExpirationOptions();
    builder.Configuration.GetSection("TransferExpiration").Bind(expirationOptions);
    expirationOptions.Validate();

    builder.Services.AddSingleton(expirationOptions);
    builder.Services.AddSingleton<TransferExpirationState>();
    builder.Services.AddSingleton<TransferExpirationService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TransferExpirationService>());

    // ─── Email SMTP (notificări la transfer primit) ────────────────────────────
    // Parola vine preferabil din variabila de mediu MAI_SMTP_PASSWORD, nu din
    // appsettings.json care ajunge în Git.
    var smtpOptions = new SmtpOptions();
    builder.Configuration.GetSection("Smtp").Bind(smtpOptions);

    var smtpPasswordFromEnv = Environment.GetEnvironmentVariable("MAI_SMTP_PASSWORD");
    if (!string.IsNullOrWhiteSpace(smtpPasswordFromEnv))
        smtpOptions.Password = smtpPasswordFromEnv;

    if (!smtpOptions.IsConfigured)
    {
        // Valorile-șablon („YOUR_SMTP_HOST_HERE”) contează ca lipsă: altfel
        // serviciul încerca conexiuni spre un host inexistent la fiecare transfer.
        // Fără SMTP, conturile noi pornesc direct confirmate, cu parolă temporară
        // (vezi UsersController.CreateUser) — nu rămân blocate așteptând un email.
        Log.Warning(
            "SMTP nu este configurat; notificările email și invitațiile sunt dezactivate. " +
            "Câmpuri lipsă sau rămase pe valoarea-șablon: {Missing}",
            string.Join(", ", smtpOptions.MissingFields()));
    }
    else
    {
        Log.Information(
            "SMTP: {Host}:{Port} ({Mode}), expeditor {From}",
            smtpOptions.Host, smtpOptions.Port,
            smtpOptions.UseSsl ? "SSL implicit" : "STARTTLS", smtpOptions.From);
    }

    builder.Services.AddSingleton(smtpOptions);
    builder.Services.AddSingleton<IEmailService, SmtpEmailService>();

    // Invitație cont nou — Scoped (are nevoie de AppDbContext, care e Scoped).
    builder.Services.AddScoped<IInvitationService, InvitationService>();

    // Trimiterea în fundal a invitației la crearea contului. Singleton, dar
    // fiecare trimitere își creează propriul scope (deci propriul AppDbContext):
    // scope-ul cererii HTTP se închide la răspuns și nu poate fi folosit după.
    builder.Services.AddSingleton<IInvitationDispatcher, InvitationDispatcher>();

    var app = builder.Build();

    // Raport de pornire — util ca să vezi ce buget de memorie ai setat.
    app.Logger.LogInformation(
        "Argon2id: profil implicit={Default}, privilegiat={Privileged}, max concurent={Max}, memorie de varf={Mem} MiB",
        argon2Options.DefaultProfile,
        argon2Options.PrivilegedProfile,
        argon2Options.MaxConcurrentHashes,
        argon2Options.EstimatedPeakMemoryMib);

    app.Logger.LogInformation(
        "Stocare fisiere: {Provider}, limita {Limit} MB, URL presemnat {Presigned}",
        app.Services.GetRequiredService<IFileStorage>().ProviderName,
        storageOptions.MaxFileSizeMb,
        storageOptions.UsePresignedDownload ? "activat" : "dezactivat");

    app.Logger.LogInformation(
        "Expirare transferuri: {State}, la fiecare {Interval} min, purjare obiecte {Purge}",
        expirationOptions.Enabled ? "activata" : "DEZACTIVATA",
        expirationOptions.IntervalMinutes,
        expirationOptions.PurgeObjects ? "activata" : "dezactivata");

    app.Logger.LogInformation(
        "JWT: issuer={Issuer}, audience={Audience}, acces {Minutes} min, refresh {Days} zile, validare issuer/audience ACTIVA",
        jwtOptions.Issuer, jwtOptions.Audience, jwtOptions.AccessTokenMinutes, jwtOptions.RefreshTokenDays);

    app.Logger.LogInformation(
        "Restrictie intranet: {State}{Mode}. 2FA obligatoriu pe endpointurile privilegiate: {Required}",
        intranetOptions.Enabled ? "activata" : "dezactivata",
        intranetOptions.Enabled && intranetOptions.AuditOnly ? " (doar audit)" : string.Empty,
        twoFactorOptions.RequiredForPrivilegedRoles ? "DA" : "nu");

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    if (rateLimitOptions.BehindReverseProxy)
        app.UseForwardedHeaders();

    // O linie per cerere HTTP, cu durata și codul de răspuns. Pusă DUPĂ
    // UseForwardedHeaders (altfel ClientIp ar fi IP-ul proxy-ului) și ÎNAINTEA
    // filtrului de intranet și a rate limiter-ului, ca respingerile lor (403, 429)
    // să apară în loguri — sunt exact cererile care contează într-o investigație.
    //
    // Calea se loghează fără query string (implicit la Serilog). Antetul
    // Authorization și corpul cererii nu se loghează niciodată: ar pune tokenuri
    // și parole într-un loc cu mult mai puține controale de acces decât baza.
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (http, _, ex) =>
        {
            if (ex is not null || http.Response.StatusCode >= 500)
                return LogEventLevel.Error;

            // Health check-ul Docker lovește API-ul la fiecare 30 de secunde. La
            // Information, logul ar fi dominat de el; la Verbose e filtrat implicit.
            if (http.Request.Path.StartsWithSegments("/api/health"))
                return LogEventLevel.Verbose;

            // Refuzurile de autentificare, autorizare și rate limit sunt semnale de
            // securitate, nu trafic obișnuit.
            return http.Response.StatusCode is 401 or 403 or 429
                ? LogEventLevel.Warning
                : LogEventLevel.Information;
        };

        options.EnrichDiagnosticContext = (diagnostic, http) =>
        {
            diagnostic.Set("RequestId", http.TraceIdentifier);
            diagnostic.Set("TraceId", Activity.Current?.TraceId.ToString());
            diagnostic.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());

            // Identitatea vine din tokenul deja validat. Pentru cererile anonime
            // (login, health) proprietățile lipsesc, nu apar ca șir gol.
            var userId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is not null)
            {
                diagnostic.Set("UserId", userId);
                diagnostic.Set("Username", http.User.Identity?.Name);
            }
        };
    });

    // Perimetrul, înaintea oricărei alte prelucrări: o cerere din afara intranetului
    // nu merită nici un ciclu de Argon2, nici un slot de rate limit.
    // Trebuie să vină DUPĂ UseForwardedHeaders, altfel filtrează după IP-ul proxy-ului.
    app.UseIntranetOnly();

    // Antetele de securitate se pun înaintea rutării, ca să ajungă și pe răspunsurile
    // generate de middleware (429 de la rate limiter, 403 de la filtrul de intranet),
    // nu doar pe cele produse de controllere.
    app.UseSecurityHeaders();

    app.UseHttpsRedirection();
    app.UseCors("AllowFrontend");
    app.UseRateLimiter();          // înainte de autentificare: respingem devreme, ieftin
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException e aruncată intenționat de `dotnet ef` când pornește
    // aplicația doar ca să citească modelul. Nu e o eroare și nu se loghează ca
    // Fatal — altfel fiecare `dotnet ef migrations add` ar părea un crash.
    Log.Fatal(ex, "SGDM API s-a oprit la pornire: {Reason}", ex.Message);

    // Cod de ieșire nenul: docker, systemd și CI trebuie să vadă eșecul.
    Environment.ExitCode = 1;
}
finally
{
    // Golește buffer-ul consolei. Fără asta, exact ultimul mesaj — cel care
    // explică de ce s-a oprit procesul — se poate pierde.
    Log.CloseAndFlush();
}