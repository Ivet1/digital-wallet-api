using System.Net;
using System.Net.Http.Json;
using DigitalWallet.Api.Models;
using Xunit;

namespace DigitalWallet.Tests;

public class WalletIntegrationTests : ApiTestBase
{
    [Fact]
    public async Task CreateIncome_And_CheckBalance_ShouldSucceed()
    {
        var request = new CreateTransactionRequest("Monthly Salary", 2000.00m, "Salary");
        var postResponse = await Client.PostAsJsonAsync("/wallet/transactions", request);
        Assert.True(postResponse.IsSuccessStatusCode);

        var balanceResponse = await Client.GetAsync("/wallet/balance");
        Assert.True(balanceResponse.IsSuccessStatusCode);

        var balanceData = await balanceResponse.Content.ReadFromJsonAsync<BalanceResponseDto>();
        Assert.NotNull(balanceData);
        Assert.Equal(2000.00m, balanceData.CurrentBalance);
        Assert.Equal(2000.00m, balanceData.TotalIncome);
        Assert.Equal(0m, balanceData.TotalExpense);
    }

    [Fact]
    public async Task CreateExpense_WithInsufficientFunds_ShouldReturnUnprocessableEntity()
    {
        var request = new CreateTransactionRequest("Expensive Laptop", -1500.00m, "Shopping");
        var postResponse = await Client.PostAsJsonAsync("/wallet/transactions", request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, postResponse.StatusCode);
    }

    [Fact]
    public async Task CreateTransaction_WithInvalidData_ShouldReturnValidationProblem()
    {
        var request = new CreateTransactionRequest("", 0m, "InvalidCategory");
        var postResponse = await Client.PostAsJsonAsync("/wallet/transactions", request);

        Assert.Equal(HttpStatusCode.BadRequest, postResponse.StatusCode);
    }

    [Fact]
    public async Task GetTransactions_And_Categories_ShouldSucceed()
    {
        var categoriesResponse = await Client.GetAsync("/wallet/categories");
        Assert.True(categoriesResponse.IsSuccessStatusCode);

        var transactionsResponse = await Client.GetAsync("/wallet/transactions");
        Assert.True(transactionsResponse.IsSuccessStatusCode);
    }

    private record BalanceResponseDto(
        decimal CurrentBalance,
        decimal TotalIncome,
        decimal TotalExpense,
        string Currency
    );
}