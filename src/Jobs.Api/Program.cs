using System.Text.Json.Serialization;
using Jobs.Api.Endpoints;
using Jobs.Api.Http;
using Jobs.Core.Persistence;

// The API never changes the schema. Migrations run separately: `dotnet ef database update` locally,
// or the EF Core migrations bundle (src/Jobs.Core/Migrations.Dockerfile) as a deployment step.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddJobsPersistence(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Jobs")
    ?? throw new InvalidOperationException("Connection string 'Jobs' is not configured."));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<JobsDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Craft Jobs API v1"));
app.MapHealthChecks("/health");
app.MapJobEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
