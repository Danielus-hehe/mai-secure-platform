using MAI.Api.BackgroundJobs;
using MAI.Api.Middleware;
using MAI.Api.Security;
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
using System.Net;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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
builder.Services.AddSingleton(storageOptions);

if (storageOptions.IsS3)
    builder.Services.AddSingleton<IFileStorage, S3FileStorage>();
else
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

// ─── Kestrel și multipart — limita de upload ───────────────────────────────
// Cele două limite trebuie ținute sincron cu Storage:MaxFileSizeMb și cu
// atributul [RequestSizeLimit] de pe endpointul de upload. Dacă diverg,
// utilizatorul primește o eroare de rețea opacă în loc de un mesaj clar.

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = storageOptions.MaxFileSizeBytes + 1_048_576; // +1 MB antet multipart
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
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key lipsește din appsettings!");

// HS256 cu o cheie de 20 de caractere se poate sparge offline. Verificarea
// oprește pornirea în loc să lase serverul să ruleze cu tokenuri falsificabile.
if (Encoding.UTF8.GetByteCount(jwtKey) < 32 || jwtKey == "YOUR_JWT_SECRET_KEY_HERE")
{
    throw new InvalidOperationException(
        "Jwt:Key trebuie să aibă cel puțin 32 de octeți de entropie reală și să nu fie valoarea-șablon. " +
        "Generați: openssl rand -base64 48");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer           = false,
            ValidateAudience         = false,
            ClockSkew                = TimeSpan.Zero,
        };
    });

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
              .WithExposedHeaders("Retry-After")   // frontend-ul îl citește la 429/503
              .AllowCredentials();
    });
});

// ─── Database ──────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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
    "Restrictie intranet: {State}{Mode}. 2FA optional: disponibil, cerut pentru roluri privilegiate = {Required}",
    intranetOptions.Enabled ? "activata" : "dezactivata",
    intranetOptions.Enabled && intranetOptions.AuditOnly ? " (doar audit)" : string.Empty,
    twoFactorOptions.RequiredForPrivilegedRoles);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (rateLimitOptions.BehindReverseProxy)
    app.UseForwardedHeaders();

// Perimetrul, înaintea oricărei alte prelucrări: o cerere din afara intranetului
// nu merită nici un ciclu de Argon2, nici un slot de rate limit.
// Trebuie să vină DUPĂ UseForwardedHeaders, altfel filtrează după IP-ul proxy-ului.
app.UseIntranetOnly();

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseRateLimiter();          // înainte de autentificare: respingem devreme, ieftin
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();