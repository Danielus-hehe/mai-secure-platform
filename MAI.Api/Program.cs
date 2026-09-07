using MAI.DataAccessLayer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

    // NB: fără MapType<IFormFile> — Swashbuckle mapează nativ IFormFile la
    // string/binary când e proprietate a unui model [FromForm] (vezi MAI.Api/Models).

    // FIX 2: Guid explicit (Swashbuckle poate da 500 pe Guid în [FromForm])
    options.MapType<Guid>(() => new OpenApiSchema
    {
        Type    = "string",
        Format  = "uuid",
        Example = new Microsoft.OpenApi.Any.OpenApiString("00000000-0000-0000-0000-000000000000"),
    });

    // FIX 3: dacă există rute duplicate/ambigue, ia prima în loc să crape
    options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());

    // FIX 4: ID-uri unice pentru scheme (evită conflicte între tipuri cu același nume)
    options.CustomSchemaIds(type => type.FullName?.Replace("+", "_") ?? type.Name);

    // JWT Bearer în Swagger UI
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name        = "Authorization",
        Type        = SecuritySchemeType.Http,
        Scheme      = "bearer",
        BearerFormat = "JWT",
        In          = ParameterLocation.Header,
        Description = "Introduceți tokenul JWT. Exemplu: Bearer eyJ...",
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
              .AllowCredentials();
    });
});

// ─── Database ──────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();