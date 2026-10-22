var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

var incomeCategories = new[] { "Salary", "Investment", "Freelance", "Gift", "Other Income" };
var expenseCategories = new[] { "Food", "Transport", "Utilities", "Entertainment", "Shopping", "Rent", "Other Expense" };

var transactions = new List<Transaction>
{
    new(Guid.NewGuid(), "Monthly Salary", 2500.00m, "Income", "Salary", DateTime.UtcNow.AddDays(-5)),
    new(Guid.NewGuid(), "Supermarket Weekly", -185.50m, "Expense", "Food", DateTime.UtcNow.AddDays(-3)),
    new(Guid.NewGuid(), "Metro Card", -25.00m, "Expense", "Transport", DateTime.UtcNow.AddDays(-2)),
    new(Guid.NewGuid(), "Electricity Bill", -65.00m, "Expense", "Utilities", DateTime.UtcNow.AddDays(-1)),
    new(Guid.NewGuid(), "Cinema & Drinks", -35.00m, "Expense", "Entertainment", DateTime.UtcNow)
};

app.MapGet("/wallet/balance", () =>
{
    var balance = transactions.Sum(t => t.Amount);
    var totalIn = transactions.Where(t => t.Amount > 0).Sum(t => t.Amount);
    var totalOut = transactions.Where(t => t.Amount < 0).Sum(t => t.Amount);

    return Results.Ok(new
    {
        CurrentBalance = balance,
        TotalIncome = totalIn,
        TotalExpense = totalOut,
        Currency = "EUR"
    });
})
.WithName("GetBalance");

app.MapGet("/wallet/transactions", (string? search, string? category) =>
{
    var query = transactions.AsEnumerable();

    if (!string.IsNullOrWhiteSpace(search))
    {
        query = query.Where(t => t.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(category))
    {
        query = query.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    return Results.Ok(query.OrderByDescending(t => t.CreatedAt).ToList());
})
.WithName("GetTransactions");

app.MapPost("/wallet/transactions", (CreateTransactionRequest request) =>
{
    if (request.Amount == 0)
        return Results.BadRequest("Amount cannot be zero.");

    var isIncome = request.Amount > 0;
    var allowed = isIncome ? incomeCategories : expenseCategories;
    var category = allowed.FirstOrDefault(c => c.Equals(request.Category, StringComparison.OrdinalIgnoreCase)) ?? (isIncome ? "Other Income" : "Other Expense");

    var transaction = new Transaction(
        Guid.NewGuid(),
        request.Description,
        request.Amount,
        isIncome ? "Income" : "Expense",
        category,
        DateTime.UtcNow
    );

    transactions.Add(transaction);
    return Results.Created($"/wallet/transactions/{transaction.Id}", transaction);
})
.WithName("CreateTransaction");

app.MapGet("/wallet/analytics", (string? period) =>
{
    var now = DateTime.UtcNow;
    var filtered = period?.ToLower() switch
    {
        "weekly" => transactions.Where(t => t.CreatedAt >= now.AddDays(-7)),
        "yearly" => transactions.Where(t => t.CreatedAt.Year == now.Year),
        _ => transactions.Where(t => t.CreatedAt.Month == now.Month && t.CreatedAt.Year == now.Year)
    };

    var list = filtered.ToList();
    var totalIncome = list.Where(t => t.Amount > 0).Sum(t => t.Amount);
    var totalExpense = Math.Abs(list.Where(t => t.Amount < 0).Sum(t => t.Amount));

    double spentRatio = totalIncome > 0 ? (double)(totalExpense / totalIncome) * 100 : 0;
    double savingsRate = totalIncome > 0 ? Math.Max(0, 100 - spentRatio) : 0;

    var expenseList = list.Where(t => t.Amount < 0).ToList();
    var avgExpense = expenseList.Any() ? expenseList.Average(t => Math.Abs(t.Amount)) : 0;
    var peakExpense = expenseList.Any() ? expenseList.OrderBy(t => t.Amount).FirstOrDefault() : null;

    var categoryBreakdown = expenseList
        .GroupBy(t => t.Category)
        .Select(g => new {
            Category = g.Key,
            Total = Math.Abs(g.Sum(t => t.Amount)),
            Percentage = totalExpense > 0 ? Math.Round((double)(Math.Abs(g.Sum(t => t.Amount)) / totalExpense) * 100, 1) : 0
        })
        .OrderByDescending(c => c.Total)
        .ToList();

    return Results.Ok(new
    {
        Period = period ?? "monthly",
        TotalIncome = totalIncome,
        TotalExpense = totalExpense,
        SpentRatioPercentage = Math.Round(spentRatio, 1),
        SavingsRatePercentage = Math.Round(savingsRate, 1),
        AverageExpense = Math.Round(avgExpense, 2),
        PeakExpense = peakExpense != null ? new { peakExpense.Description, Amount = Math.Abs(peakExpense.Amount), peakExpense.Category } : null,
        TopExpenseCategories = categoryBreakdown,
        IncomeCategories = incomeCategories,
        ExpenseCategories = expenseCategories
    });
})
.WithName("GetAnalytics");

app.MapGet("/wallet/export/csv", () =>
{
    var csv = new System.Text.StringBuilder();
    csv.AppendLine("Id,Description,Amount,Type,Category,CreatedAt");

    foreach (var t in transactions)
    {
        csv.AppendLine($"{t.Id},{escapeCsv(t.Description)},{t.Amount},{t.Type},{escapeCsv(t.Category)},{t.CreatedAt:yyyy-MM-dd HH:mm:ss}");
    }

    var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
    return Results.File(bytes, "text/csv", "wallet-transactions.csv");
})
.WithName("ExportCsv");

app.Run();

static string escapeCsv(string field) => $"\"{field.Replace("\"", "\"\"")}\"";

record Transaction(Guid Id, string Description, decimal Amount, string Type, string Category, DateTime CreatedAt);
record CreateTransactionRequest(string Description, decimal Amount, string Category);