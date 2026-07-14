using FlightReadinessEngine.Api.Agents;
using FlightReadinessEngine.Api.Cache;
using FlightReadinessEngine.Api.Master;
using FlightReadinessEngine.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- GCP authentication (single global source of truth) ---
// Preferred: Application Default Credentials (ADC). If an access token is
// supplied (e.g. a short-lived token from Cloud Shell) it is used instead.
// Provide the token via appsettings.json ("Gcp:AccessToken") or the
// GCP_ACCESS_TOKEN environment variable.
var accessToken = builder.Configuration["Gcp:AccessToken"]
    ?? Environment.GetEnvironmentVariable("GCP_ACCESS_TOKEN");

GcpAuth.Initialize(accessToken);

if (GcpAuth.HasToken)
{
    Console.WriteLine("GCP auth: using supplied access token.");
}
else
{
    Console.WriteLine("GCP auth: using Application Default Credentials (ADC).");
}

// Legacy service-account file support (optional; only used when no token/ADC).
var configuredCredentialsPath = builder.Configuration["Gcp:CredentialsPath"];
if (!GcpAuth.HasToken && !string.IsNullOrWhiteSpace(configuredCredentialsPath))
{
    if (!Path.IsPathRooted(configuredCredentialsPath))
    {
        configuredCredentialsPath = Path.Combine(builder.Environment.ContentRootPath, configuredCredentialsPath);
    }

    if (File.Exists(configuredCredentialsPath))
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", configuredCredentialsPath);
    }
    else
    {
        Console.WriteLine($"Warning: GCP credentials file not found at {configuredCredentialsPath}");
    }
}

var projectId = builder.Configuration["Gcp:ProjectId"]
    ?? Environment.GetEnvironmentVariable("GCP_PROJECT_ID")
    ?? "qwiklabs-gcp-04-509f741dc909";

var location = builder.Configuration["Gcp:Location"]
    ?? Environment.GetEnvironmentVariable("GCP_LOCATION")
    ?? "US";

// Vertex AI requires a specific region (e.g. us-central1). The BigQuery
// "Location" (e.g. "US") is a multi-region and is NOT a valid Vertex host,
// so Vertex uses its own setting.
var vertexLocation = builder.Configuration["Gcp:VertexLocation"]
    ?? Environment.GetEnvironmentVariable("GCP_VERTEX_LOCATION")
    ?? "us-central1";

Environment.SetEnvironmentVariable("GCP_PROJECT_ID", projectId);
Environment.SetEnvironmentVariable("GCP_LOCATION", location);
Environment.SetEnvironmentVariable("GCP_VERTEX_LOCATION", vertexLocation);

var cacheKind = builder.Configuration["Gcp:CacheKind"]
    ?? Environment.GetEnvironmentVariable("CACHE_KIND")
    ?? "MultiAgentCacheMatrix";

builder.Services.AddSingleton<IAgentCache>(_ => new DatastoreCacheService(projectId, cacheKind));

// Add HttpClient for Digital Twin service
builder.Services.AddHttpClient("DigitalTwin", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddScoped<FlightService>();
builder.Services.AddScoped<CascadeImpactService>();
builder.Services.AddScoped<TailDetailsService>();
builder.Services.AddScoped<CrewAgent>();
builder.Services.AddScoped<PartsAgent>();
builder.Services.AddScoped<MaintenanceAgent>();
builder.Services.AddScoped<GroundAgent>();
builder.Services.AddScoped<FlightPlanningAgent>();
builder.Services.AddScoped<AircraftAgent>();
builder.Services.AddScoped<OperationManageAgent>();
builder.Services.AddScoped<InfographicAgent>();
builder.Services.AddScoped<IAgentIntentClassifier, AgentIntentClassifier>();
builder.Services.AddScoped<MasterChatService>();
builder.Services.AddScoped<IDigitalTwinService, DigitalTwinService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
// Swagger is exposed in all environments (including Cloud Run) so the API
// surface is discoverable at /swagger.
app.UseSwagger();
app.UseSwaggerUI();

if (app.Environment.IsDevelopment())
{
    // TLS is terminated by the Cloud Run proxy in production, so only
    // redirect to HTTPS when running locally.
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();
app.Run();
