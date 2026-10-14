using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Jobs.Tests.Integration;

/// <summary>Hosts the real API in memory, pointed at the test container.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("ConnectionStrings:Jobs", connectionString);
}
