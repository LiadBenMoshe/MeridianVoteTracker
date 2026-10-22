using System.Reflection;
using System.Text.Json.Serialization;
using Azure.Identity;
using Meridian.Api.Infrastructure;
using Meridian.Api.Services;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Build-time OpenAPI generation (GetDocument) only needs the endpoint metadata, not a database.
var generatingOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

// Secrets: environment variables / user-secrets locally, Azure Key Vault when KeyVault__Uri is set.
// Secret names use "--" for ":" (e.g. ConnectionStrings--Meridian).
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } vaultUri && !generatingOpenApi)
    builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true; // CorrelationId / ImportId on every line
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Env var name, or "ApplicationInsights--ConnectionString" when it comes from Key Vault (no '_' allowed there).
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]) ||
    !string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
    // Send our own Information logs (import/snapshot/extraction trail with CorrelationId scope), not only warnings.
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>("Meridian", LogLevel.Information);
    builder.Services.AddSingleton<ITelemetryInitializer, CorrelationTelemetryInitializer>();
}

if (generatingOpenApi && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Meridian")))
    builder.Configuration["ConnectionStrings:Meridian"] = "Host=openapi-generation";
builder.Services.AddMeridianInfrastructure(builder.Configuration, runWorkers: !generatingOpenApi && builder.Configuration.GetValue("Workers:Enabled", true));
builder.Services.AddScoped<ConflictService>();
builder.Services.AddScoped<VoteService>();
builder.Services.AddScoped<SnapshotService>();

builder.Services.AddAuthentication(HeaderUserAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, HeaderUserAuthenticationHandler>(HeaderUserAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(Policies.Configure);

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ApiExceptionHandler.Customize);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Same enum format for ProblemDetails bodies (written via HTTP JSON options) and MVC responses.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddControllers(o =>
    {
        // JSON only: keeps the OpenAPI contract (and the generated client) to one media type.
        o.OutputFormatters.RemoveType<Microsoft.AspNetCore.Mvc.Formatters.StringOutputFormatter>();
        o.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesAttribute("application/json"));
    })
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Meridian Vote Tracker API", Version = "v1" });
    o.SupportNonNullableReferenceTypes();
    o.SchemaFilter<RequiredDtoSchemaFilter>();
    o.UseAllOfToExtendReferenceSchemas();
    o.MapType<DateOnly>(() => new OpenApiSchema { Type = "string", Format = "date" });
    o.AddSecurityDefinition("XUser", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = HeaderUserAuthenticationHandler.HeaderName,
        Description = "Simplified login: dana, omar (analysts) or lena (admin).",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "XUser" } }] = [],
    });
    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml);
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(Correlation.HeaderName, "Location")));

var app = builder.Build();

if (!generatingOpenApi && app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    // Device-code sign-in (Docker dev setup) happens once here, before anything talks to Azure.
    await app.Services.GetRequiredService<AzureCredentials>().EnsureSignedInAsync(AzureCredentials.ScopesFor(app.Configuration), CancellationToken.None);
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Meridian v1"));
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().ExcludeFromDescription();

app.Run();

/// <summary>Adds the correlation id to every Application Insights item.</summary>
internal sealed class CorrelationTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        if (Correlation.Id is { } id && telemetry is ISupportProperties p) p.Properties["CorrelationId"] = id;
    }
}

public partial class Program;
