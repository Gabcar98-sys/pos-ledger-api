using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PosLedger.Api.Common;
using PosLedger.Api.Features.Auth;
using PosLedger.Api.Features.Imports;
using PosLedger.Api.Features.Products;
using PosLedger.Api.Features.Reconciliation;
using PosLedger.Api.Features.Sales;
using PosLedger.Api.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ────────────────────────────────────────────────────────────────────
// Plain text locally so it is readable, compact JSON everywhere else so a log
// aggregator can index the correlation id instead of regex-ing a message.
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext();

    if (context.HostingEnvironment.IsDevelopment())
    {
        configuration.WriteTo.Console();
    }
    else
    {
        configuration.WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter());
    }
});

// ── Persistence ────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<PosLedgerDbContext>(options =>
    options
        .UseNpgsql(DatabaseConnection.Resolve(builder.Configuration), npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(PosLedgerDbContext).Assembly.FullName);
            // Managed Postgres drops connections; a transient failure should cost a retry, not a 500.
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
        })
        // snake_case everywhere: the database is also read by hand, by psql and by whatever
        // reporting tool the client already owns. Quoted "StockOnHand" columns are a daily tax.
        .UseSnakeCaseNamingConvention());

// ── Authentication ─────────────────────────────────────────────────────────────
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(jwt => jwt.Secret.Length >= 32, "Jwt:Secret must be at least 32 characters.")
    .Validate(
        jwt => builder.Environment.IsDevelopment()
               || builder.Environment.EnvironmentName == "Testing"
               || jwt.Secret != JwtOptions.DevelopmentSecret,
        "Jwt:Secret is still the development value. Set a real secret before deploying.")
    // Fails at boot rather than on the first login attempt. A misconfigured deployment
    // that starts and then rejects everyone is harder to diagnose than one that never starts.
    .ValidateOnStart();

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection.Issuer,
            ValidAudience = jwtSection.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection.Secret)),
            // Default is five minutes of slack on expiry, which is five minutes longer than
            // a revoked token should be usable.
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.Admin, policy => policy.RequireRole(Roles.Admin));

// ── Web ────────────────────────────────────────────────────────────────────────
builder.Services.AddValidatorsFromAssemblyContaining<CreateProductValidator>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums travel as names. "Sale" survives a schema change; 1 does not.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails(options =>
{
    // Every error response carries the same correlation id the logs do.
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "POS Ledger API",
        Version = "v1",
        Description = "Inventory and invoicing ledger for a point of sale. "
                      + "Stock only ever moves through an append-only ledger."
    });

    // Lets the Swagger UI hold a token, so the whole API can be exercised from the browser
    // without reaching for curl.
    var scheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste the accessToken returned by POST /api/v1/auth/token.",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = JwtBearerDefaults.AuthenticationScheme
        }
    };

    options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, scheme);
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement { [scheme] = [] });
});

var app = builder.Build();

// ── Pipeline ───────────────────────────────────────────────────────────────────
app.UseExceptionHandler();      // unhandled exceptions come out as ProblemDetails, not HTML
app.UseStatusCodePages();
app.UseCorrelationId();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

// Swagger is on in every environment on purpose: this API is meant to be opened and
// poked at by someone who has just found the repository.
app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "POS Ledger API v1"));
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Liveness answers "is the process up" and must not touch the database — otherwise a
// database blip makes the orchestrator kill a perfectly healthy container.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithTags("Diagnostics")
    .WithSummary("Liveness probe");

// Readiness answers "can it serve traffic", which does need the database.
app.MapGet("/ready", async (PosLedgerDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .WithTags("Diagnostics")
    .WithSummary("Readiness probe");

app.MapAuth();
app.MapProducts();
app.MapSales();
app.MapImports();
app.MapReconciliation();

await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

app.Run();

/// <summary>Exposed so the integration tests can boot the real pipeline with WebApplicationFactory.</summary>
public partial class Program;
