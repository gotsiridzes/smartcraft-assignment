using Jobs.Core.Persistence;
using Jobs.Worker;
using Jobs.Worker.Accounting;

var builder = Host.CreateApplicationBuilder(args);

// JSON lines with scopes, so TaskId / JobId / WorkerId / Attempt follow one export across instances.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(console =>
{
    console.IncludeScopes = true;
    console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    console.UseUtcTimestamp = true;
});

builder.Services.AddJobsPersistence(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Jobs")
    ?? throw new InvalidOperationException("Connection string 'Jobs' is not configured."));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<ExportWorkerOptions>()
    .Bind(builder.Configuration.GetSection(ExportWorkerOptions.SectionName))
    .Validate(o => o.IsValid(), "ExportWorker settings are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<FakeAccountingOptions>()
    .Bind(builder.Configuration.GetSection(FakeAccountingOptions.SectionName))
    .Validate(o => o.IsValid(), "FakeAccounting settings are invalid.")
    .ValidateOnStart();

builder.Services.AddSingleton<IAccountingClient, FakeAccountingClient>();
builder.Services.AddSingleton<TaskClaimer>();
builder.Services.AddSingleton<ExportProcessor>();
builder.Services.AddHostedService<ExportWorker>();

await builder.Build().RunAsync();
