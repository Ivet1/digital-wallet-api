using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Връзка с PostgreSQL в Kubernetes (postgres-service)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql("Host=postgres-service;Database=walletdb;Username=postgres;Password=secretpassword"));

builder.Services.AddOpenApi();

var app = builder.Build();

// 2. Автоматично създаване на таблиците при старт
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// 3. Поддръжка на фронтенда (wwwroot папка)
app.UseStaticFiles();

var incomeCategories = new[] { "Salary", "Investment", "Freelance", "Gift", "Other Income" };
var expenseCategories = new[] { "Food", "Transport", "Utilities", "Entertainment", "Shopping", "Rent", "Other Expense" };

// 4. API Ендпоинти, които четат и пишат в истинската база данни
app.MapGet("/wallet/balance", async (AppDbContext db) =>
{
    var transactions = await db.Transactions.ToListAsync();
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

app.MapGet("/wallet/transactions", async (string? search, string? category, AppDbContext db) =>
{
    var query = db.Transactions.AsQueryable();

    if (!string.IsNullOrWhiteSpace(search))
        query = query.Where(t => t.Description.Contains(search));

    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(t => t.Category.ToLower() == category.ToLower());

    var result = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    return Results.Ok(result);
})
.WithName("GetTransactions");

app.MapPost("/wallet/transactions", async (CreateTransactionRequest request, AppDbContext db) =>
{
    if (request.Amount == 0)
        return Results.BadRequest("Amount cannot be zero.");

    var isIncome = request.Amount > 0;
    var allowed = isIncome ? incomeCategories : expenseCategories;
    var category = allowed.FirstOrDefault(c => c.Equals(request.Category, StringComparison.OrdinalIgnoreCase)) ?? (isIncome ? "Other Income" : "Other Expense");

    var transaction = new TransactionEntity
    {
        Id = Guid.NewGuid(),
        Description = request.Description,
        Amount = request.Amount,
        Type = isIncome ? "Income" : "Expense",
        Category = category,
        CreatedAt = DateTime.UtcNow
    };

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync();

    return Results.Created($"/wallet/transactions/{transaction.Id}", transaction);
})
.WithName("CreateTransaction");

app.MapGet("/wallet/analytics", async (string? period, AppDbContext db) =>
{
    var allTransactions = await db.Transactions.ToListAsync();
    var now = DateTime.UtcNow;

    var filtered = period?.ToLower() switch
    {
        "weekly" => allTransactions.Where(t => t.CreatedAt >= now.AddDays(-7)),
        "yearly" => allTransactions.Where(t => t.CreatedAt.Year == now.Year),
        _ => allTransactions.Where(t => t.CreatedAt.Month == now.Month && t.CreatedAt.Year == now.Year)
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

app.MapGet("/wallet/export/csv", async (AppDbContext db) =>
{
    var transactions = await db.Transactions.ToListAsync();
    var csv = new System.Text.StringBuilder();
    csv.AppendLine("Id,Description,Amount,Type,Category,CreatedAt");

    foreach (var t in transactions)
    {
        csv.AppendLine($"{t.Id},{EscapeCsv(t.Description)},{t.Amount},{t.Type},{EscapeCsv(t.Category)},{t.CreatedAt:yyyy-MM-dd HH:mm:ss}");
    }

    var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
    return Results.File(bytes, "text/csv", "wallet-transactions.csv");
})
.WithName("ExportCsv");

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));

app.MapFallbackToFile("index.html");

app.Run();

// 5. Помощен метод и заявка (само те остават най-долу)
static string EscapeCsv(string field) => $"\"{field.Replace("\"", "\"\"")}\"";

record CreateTransactionRequest(string Description, decimal Amount, string Category);