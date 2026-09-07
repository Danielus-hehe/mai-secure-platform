using MAI.Api.Security;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Security;
using MAI.BusinessLogic.Services;
using MAI.DataAccessLayer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// ─── Kestrel — 50 MB pentru upload fișiere ────────────────────────────────
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 52_428_800;
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

builder.Services.AddSingleton(argon2Options);
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

// ─── Politica de parole ────────────────────────────────────────────────────
var passwordPolicyOptions = new PasswordPolicyOptions();
builder.Configuration.GetSection("PasswordPolicy").Bind(passwordPolicyOptions);
builder.Services.AddSingleton(passwordPolicyOptions);
builder.Services.AddSingleton<PasswordPolicy>();

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
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("Retry-After")   // frontend-ul îl citește la 429/503
              .AllowCredentials();
    });
});

// ─── Database ──────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Raport de pornire — util ca să vezi ce buget de memorie ai setat.
app.Logger.LogInformation(
    "Argon2id: profil implicit={Default}, privilegiat={Privileged}, max concurent={Max}, memorie de varf={Mem} MiB",
    argon2Options.DefaultProfile,
    argon2Options.PrivilegedProfile,
    argon2Options.MaxConcurrentHashes,
    argon2Options.EstimatedPeakMemoryMib);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (rateLimitOptions.BehindReverseProxy)
    app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseRateLimiter();          // înainte de autentificare: respingem devreme, ieftin
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();