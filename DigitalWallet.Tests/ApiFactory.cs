using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace DigitalWallet.Tests;

/// <summary>
/// One real PostgreSQL container and one running API for the whole test run (started once, shared by
/// every test class through the "api" collection). Each test starts from an empty table, see ApiTestBase.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string ConnectionStringVariable = "ConnectionStrings__Default";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("walletdb_test")
        .WithUsername("postgres")
        .WithPassword("secretpassword")
        .Build();

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        // Same mechanism as production (an environment variable), so the app picks it up when it is built.
        Environment.SetEnvironmentVariable(ConnectionStringVariable, _db.GetConnectionString());

        // The API creates the schema itself in the background; wait until it reports ready.
        using var client = CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var response = await client.GetAsync("/ready");
            if (response.IsSuccessStatusCode) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The API did not become ready in time (last status {(int)response.StatusCode}).");
            await Task.Delay(250);
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _db.DisposeAsync();
        Environment.SetEnvironmentVariable(ConnectionStringVariable, null);
        await base.DisposeAsync();
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
