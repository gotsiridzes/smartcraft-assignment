using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Jobs.Tests.Integration;

/// <summary>Hosts the real API in memory, pointed at the test container.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": keeps a developer's appsettings.Development.json and user secrets out of tests.
        builder.UseEnvironment("Testing");

        // Added last, so it wins over appsettings.json. The API reads the connection string lazily.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Jobs"] = connectionString }));
    }
}
