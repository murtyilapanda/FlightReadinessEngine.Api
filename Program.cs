using FlightReadinessEngine.Api.Agents;
using FlightReadinessEngine.Api.Cache;
using FlightReadinessEngine.Api.Master;
using FlightReadinessEngine.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var configuredCredentialsPath = builder.Configuration["Gcp:CredentialsPath"];
if (!string.IsNullOrWhiteSpace(configuredCredentialsPath))
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
    ?? "zinc-hour-460015-n7";

var location = builder.Configuration["Gcp:Location"]
    ?? Environment.GetEnvironmentVariable("GCP_LOCATION")
    ?? "asia-south1";

Environment.SetEnvironmentVariable("GCP_PROJECT_ID", projectId);
Environment.SetEnvironmentVariable("GCP_LOCATION", location);

var cacheKind = builder.Configuration["Gcp:CacheKind"]
    ?? Environment.GetEnvironmentVariable("CACHE_KIND")
    ?? "MultiAgentCacheMatrix";

builder.Services.AddSingleton<IAgentCache>(_ => new DatastoreCacheService(projectId, cacheKind));

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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
