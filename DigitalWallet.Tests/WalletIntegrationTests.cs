using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DigitalWallet.Tests;

[Collection("api")]
public class TransactionTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Income_is_stored_and_changes_the_balance()
    {
        var response = await Post("Monthly Salary", 2000.00m, "Salary");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var balance = await GetJson("/wallet/balance");
        Assert.Equal(2000m, balance.GetProperty("currentBalance").GetDecimal());
        Assert.Equal(2000m, balance.GetProperty("totalIncome").GetDecimal());
        Assert.Equal(0m, balance.GetProperty("totalExpense").GetDecimal());
        Assert.Equal("EUR", balance.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Expense_reduces_the_balance()
    {
        await Seed("Salary", 100m, "Salary");
        await Seed("Lunch", -30m, "Food");

        Assert.Equal(70m, await Balance());
    }

    [Fact]
    public async Task Expense_larger_than_the_balance_is_rejected_and_not_stored()
    {
        await Seed("Salary", 100m);

        var response = await Post("Laptop", -150m, "Shopping");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(100m, await Balance());
        Assert.Equal(1, (await GetJson("/wallet/transactions")).GetArrayLength());
    }

    [Fact]
    public async Task Expense_that_exactly_empties_the_wallet_is_allowed()
    {
        await Seed("Salary", 50m);

        var response = await Post("Everything", -50m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0m, await Balance());
    }

    [Fact]
    public async Task Amount_is_rounded_to_two_decimals_away_from_zero()
    {
        await Seed("Odd amount", 10.005m);

        Assert.Equal(10.01m, await Balance());
    }

    [Fact]
    public async Task Amount_that_rounds_to_zero_is_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("Dust", 0.004m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("Nothing", 0m)).StatusCode);
    }

    [Fact]
    public async Task Amount_limit_is_one_million()
    {
        Assert.Equal(HttpStatusCode.Created, (await Post("At the limit", 1_000_000m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("Over the limit", 1_000_000.01m)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_description_is_rejected(string description)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(description, 10m)).StatusCode);
    }

    [Fact]
    public async Task Description_is_trimmed_and_limited_to_120_characters()
    {
        var created = await Post("  Coffee  ", 10m);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Coffee", body.GetProperty("description").GetString());

        Assert.Equal(HttpStatusCode.Created, (await Post(new string('a', 120), 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new string('a', 121), 1m)).StatusCode);
    }

    [Fact]
    public async Task Category_is_normalised_to_its_canonical_spelling()
    {
        var response = await Post("Pay", 10m, "  sALARY ");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Salary", body.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Missing_category_gets_a_default_by_type()
    {
        var income = await (await Post("Found money", 100m)).Content.ReadFromJsonAsync<JsonElement>();
        var expense = await (await Post("Mystery", -10m)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Other Income", income.GetProperty("category").GetString());
        Assert.Equal("Income", income.GetProperty("type").GetString());
        Assert.Equal("Other Expense", expense.GetProperty("category").GetString());
        Assert.Equal("Expense", expense.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Category_must_match_the_transaction_type()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("Wrong", 10m, "Food")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("Wrong", -10m, "Salary")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("Unknown", 10m, "InvalidCategory")).StatusCode);
    }

    [Fact]
    public async Task Parallel_expenses_can_never_overdraw_the_wallet()
    {
        await Seed("Salary", 100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Post($"Expense {i}", -30m, "Food")));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(10m, await Balance());
    }
}

[Collection("api")]
public class QueryTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Categories_lists_income_and_expense_categories()
    {
        var body = await GetJson("/wallet/categories");

        Assert.Contains("Salary", body.GetProperty("income").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Food", body.GetProperty("expense").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Transactions_are_returned_newest_first()
    {
        await Seed("first", 10m);
        await Task.Delay(20);
        await Seed("second", 10m);

        var list = await GetJson("/wallet/transactions");

        Assert.Equal("second", list[0].GetProperty("description").GetString());
        Assert.Equal("first", list[1].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_matches_part_of_the_description()
    {
        await Seed("Morning Coffee", 10m);
        await Seed("Rent", 10m);

        var list = await GetJson("/wallet/transactions?search=coFFee");

        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Morning Coffee", list[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Search_treats_percent_and_underscore_literally()
    {
        await Seed("50% bonus", 10m);
        await Seed("Plain bonus", 10m);

        var list = await GetJson("/wallet/transactions?search=%25");

        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("50% bonus", list[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Category_filter_is_case_insensitive()
    {
        await Seed("Pay", 100m, "Salary");
        await Seed("Gift", 20m, "Gift");

        var list = await GetJson("/wallet/transactions?category=SALARY");

        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Salary", list[0].GetProperty("category").GetString());
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 1)]   // values below 1 are raised to 1
    [InlineData(-5, 1)]
    public async Task Limit_is_applied_and_clamped(int limit, int expected)
    {
        for (var i = 0; i < 3; i++) await Seed($"t{i}", 10m);

        var list = await GetJson($"/wallet/transactions?limit={limit}");

        Assert.Equal(expected, list.GetArrayLength());
    }

    [Fact]
    public async Task Analytics_on_an_empty_wallet_is_all_zero()
    {
        var body = await GetJson("/wallet/analytics");

        Assert.Equal("monthly", body.GetProperty("period").GetString());
        Assert.Equal(0m, body.GetProperty("totalIncome").GetDecimal());
        Assert.Equal(0m, body.GetProperty("totalExpense").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("spentRatioPercentage").ValueKind);
        Assert.Equal(0m, body.GetProperty("averageExpense").GetDecimal());
    }

    [Fact]
    public async Task Analytics_summarises_income_and_expenses()
    {
        await Seed("Salary", 1000m, "Salary");
        await Seed("Groceries", -200m, "Food");
        await Seed("Bus", -100m, "Transport");

        var body = await GetJson("/wallet/analytics");

        Assert.Equal(1000m, body.GetProperty("totalIncome").GetDecimal());
        Assert.Equal(300m, body.GetProperty("totalExpense").GetDecimal());
        Assert.Equal(30m, body.GetProperty("spentRatioPercentage").GetDecimal());
        Assert.Equal(70m, body.GetProperty("savingsRatePercentage").GetDecimal());
        Assert.Equal(150m, body.GetProperty("averageExpense").GetDecimal());

        var peak = body.GetProperty("peakExpense");
        Assert.Equal("Groceries", peak.GetProperty("description").GetString());
        Assert.Equal(200m, peak.GetProperty("amount").GetDecimal());

        var top = body.GetProperty("topExpenseCategories");
        Assert.Equal("Food", top[0].GetProperty("category").GetString());
        Assert.Equal(66.7m, top[0].GetProperty("percentage").GetDecimal());
        Assert.Equal("Transport", top[1].GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("weekly", "weekly")]
    [InlineData("yearly", "yearly")]
    [InlineData("YEARLY", "yearly")]
    [InlineData("anything-else", "monthly")]
    public async Task Analytics_period_is_recognised(string requested, string expected)
    {
        var body = await GetJson($"/wallet/analytics?period={requested}");

        Assert.Equal(expected, body.GetProperty("period").GetString());
    }

    [Fact]
    public async Task Csv_export_has_a_header_a_utf8_bom_and_neutralises_formulas()
    {
        await Seed("=SUM(1+1)", 10m);

        var response = await Client.GetAsync("/wallet/export/csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Contains("Id,Description,Amount,Type,Category,CreatedAt", text);
        Assert.Contains("\"'=SUM(1+1)\"", text);   // a leading apostrophe stops Excel from running it
        Assert.Contains(",10.00,Income,", text);
    }
}

[Collection("api")]
public class OperationalEndpointTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health")]
    [InlineData("/ready")]
    public async Task Health_endpoints_return_200(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Prometheus_metrics_are_exposed()
    {
        await Client.GetAsync("/wallet/balance");   // produce at least one request metric

        var response = await Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
