using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jobs.Core.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="NpgsqlDataSource"/> (shared by EF Core and plain SQL), an
    /// <see cref="IDbContextFactory{TContext}"/> for singletons such as the worker, and a scoped
    /// <see cref="JobsDbContext"/> for request handlers. The connection string is resolved lazily so
    /// configuration overrides applied after registration (for example in tests) are honoured.
    /// </summary>
    public static IServiceCollection AddJobsPersistence(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddSingleton(sp => new NpgsqlDataSourceBuilder(connectionString(sp))
            .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
            .Build());

        services.AddDbContextFactory<JobsDbContext>((sp, options) => options.UseJobsNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<JobsDbContext>>().CreateDbContext());

        return services;
    }

    public static DbContextOptionsBuilder UseJobsNpgsql(this DbContextOptionsBuilder options, NpgsqlDataSource dataSource)
        => options.UseNpgsql(dataSource).UseSnakeCaseNamingConvention();
}
