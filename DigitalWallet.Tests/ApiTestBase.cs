using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using DigitalWallet.Api;
using DigitalWallet.Api.Models;
using Xunit;

namespace DigitalWallet.Tests;

public abstract class ApiTestBase : IAsyncLifetime
{
    protected readonly ApiFactory Factory;
    protected readonly HttpClient Client;

    protected ApiTestBase(ApiFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Transactions.RemoveRange(db.Transactions);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task<HttpResponseMessage> Post(string description, decimal amount, string? category = null)
    {
        return await Client.PostAsJsonAsync("/wallet/transactions", new { Description = description, Amount = amount, Category = category });
    }

    protected async Task Seed(string description, decimal amount, string? category = null)
    {
        var response = await Post(description, amount, category);
        response.EnsureSuccessStatusCode();
    }

    protected async Task<decimal> Balance()
    {
        var json = await GetJson("/wallet/balance");
        return json.GetProperty("currentBalance").GetDecimal();
    }

    protected async Task<JsonElement> GetJson(string url)
    {
        return await Client.GetFromJsonAsync<JsonElement>(url);
    }
}