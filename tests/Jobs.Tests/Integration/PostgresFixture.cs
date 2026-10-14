using Jobs.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Jobs.Tests.Integration;

/// <summary>
/// One real PostgreSQL for the whole suite: SKIP LOCKED, row locks and unique indexes behave like
/// production, which in-memory providers cannot do. Tests call <see cref="ResetAsync"/> first.
/// Also serves as the <see cref="IDbContextFactory{TContext}"/> for worker components under test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime, IDbContextFactory<JobsDbContext>
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private NpgsqlDataSource? _dataSource;
    private ApiFactory? _api;

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public JobsDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>();
        options.UseJobsNpgsql(DataSource);
        return new JobsDbContext(options.Options);
    }

    /// <summary>An HTTP client for the API, hosted in memory against this database. The host is shared by all tests.</summary>
    public HttpClient CreateApiClient()
    {
        _api ??= new ApiFactory(ConnectionString);
        return _api.CreateClient();
    }

    public async Task ResetAsync()
    {
        await using var command = DataSource.CreateCommand(
            "TRUNCATE TABLE jobs, job_lines, export_tasks, idempotency_keys, fake_accounting.invoices CASCADE");
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
