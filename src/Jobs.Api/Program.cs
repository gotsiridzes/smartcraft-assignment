using System.Text.Json.Serialization;
using Jobs.Api.Endpoints;
using Jobs.Api.Http;
using Jobs.Core.Persistence;
using Microsoft.EntityFrameworkCore;

// "--migrate" turns this image into the one-shot migration step used by docker compose.
const string MigrateFlag = "--migrate";
var migrateOnly = args.Contains(MigrateFlag);

var builder = WebApplication.CreateBuilder(args.Where(arg => arg != MigrateFlag).ToArray());

builder.Services.AddJobsPersistence(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Jobs")
    ?? throw new InvalidOperationException("Connection string 'Jobs' is not configured."));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<JobsDbContext>();

var app = builder.Build();

if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied.");
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Craft Jobs API v1"));
app.MapHealthChecks("/health");
app.MapJobEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
