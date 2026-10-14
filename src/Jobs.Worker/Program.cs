using Jobs.Core.Persistence;
using Jobs.Worker;
using Jobs.Worker.Accounting;

var builder = Host.CreateApplicationBuilder(args);

// Scopes carry TaskId / JobId / WorkerId / Attempt, so one export can be followed across instances.
// Development: one readable line per entry. Elsewhere: JSON lines for a log collector.
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(console =>
    {
        console.IncludeScopes = true;
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss.fff ";
    });
}
else
{
    builder.Logging.AddJsonConsole(console =>
    {
        console.IncludeScopes = true;
        console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        console.UseUtcTimestamp = true;
    });
}

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
