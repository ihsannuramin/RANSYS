using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Providers;

namespace Ransys.Api.Tests;

/// <summary>
/// The real API host (Program) in-process: environment "Test", development authentication enabled, the test database
/// (<see cref="TestDatabaseSettings"/>) and a short provider call budget so a hanging adapter becomes IN_DOUBT quickly.
/// Scripted adapters are registered per scenario in <see cref="Registry"/>.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly string _connectionString;
    private readonly bool _developmentAuthentication;
    private readonly Action<IServiceCollection>? _services;

    public ApiFactory()
        : this("Test", TestDatabaseSettings.ConnectionString, developmentAuthentication: true, services: null)
    {
    }

    protected ApiFactory(string environment, string connectionString, bool developmentAuthentication, Action<IServiceCollection>? services)
    {
        _environment = environment;
        _connectionString = connectionString;
        _developmentAuthentication = developmentAuthentication;
        _services = services;
    }

    public InProcessProviderAdapterRegistry Registry => Services.GetRequiredService<InProcessProviderAdapterRegistry>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:TransactionDb", _connectionString);
        builder.UseSetting("Ransys:Auth:AllowDevelopmentAuthentication", _developmentAuthentication ? "true" : "false");
        builder.UseSetting("Ransys:ProviderCall:ConnectTimeoutMs", "100");
        builder.UseSetting("Ransys:ProviderCall:ReadTimeoutMs", "400");
        if (_services is not null)
        {
            builder.ConfigureTestServices(_services);
        }
    }
}

/// <summary>An API host with a different environment, database or service overrides (security and fail-closed tests).</summary>
public sealed class CustomApiFactory(string environment, string connectionString, bool developmentAuthentication, Action<IServiceCollection>? services = null)
    : ApiFactory(environment, connectionString, developmentAuthentication, services);
