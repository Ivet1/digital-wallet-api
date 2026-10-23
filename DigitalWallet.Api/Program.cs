using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddKeyPerFile("/vault/secrets", optional: true, reloadOnChange: true);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=postgres-service;Database=walletdb;Username=postgres;Password=secretpassword";

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    int maxRetries = 10;
    int delaySeconds = 3;

    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            logger.LogInformation("Attempting to connect to the database and ensure created (Attempt {Attempt}/{Max})...", i + 1, maxRetries);
            db.Database.EnsureCreated();
            logger.LogInformation("Database connected and initialized successfully!");
            break;
        }
        catch (Exception ex)
        {
            if (i == maxRetries - 1)
            {
                logger.LogError(ex, "Failed to connect to the database after maximum retries.");
                throw;
            }
            logger.LogWarning("Database not ready yet. Retrying in {Seconds} seconds...", delaySeconds);
            Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
        }
    }
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

var incomeCategories = new[] { "Salary", "Investment", "Freelance", "Gift", "Other Income" };
var expenseCategories = new[] { "Food", "Transport", "Utilities", "Entertainment", "Shopping", "Rent", "Other Expense" };

const string DefaultIncomeCategory = "Other Income";
const string DefaultExpenseCategory = "Other Expense";
const decimal MaxAmount = 1_000_000m;
const int MaxDescriptionLength = 120;
const long WalletLockKey = 827364L;


app.MapGet("/wallet/balance", async (AppDbContext db) =>
{
    var totals = await db.Transactions
        .AsNoTracking()
        .GroupBy(_ => 1)
        .Select(g => new
        {
            Income = g.Sum(t => t.Amount > 0 ? t.Amount : 0m),
            Expense = g.Sum(t => t.Amount < 0 ? -t.Amount : 0m)
        })
        .FirstOrDefaultAsync();

    var income = totals?.Income ?? 0m;
    var expense = totals?.Expense ?? 0m;

    return Results.Ok(new
    {
        CurrentBalance = income - expense,
        TotalIncome = income,
        TotalExpense = expense,
        Currency = "EUR"
    });
})
.WithName("GetBalance");

app.MapGet("/wallet/categories", () => Results.Ok(new
{
    Income = incomeCategories,
    Expense = expenseCategories
}))
.WithName("GetCategories");

app.MapGet("/wallet/transactions", async (string? search, string? category, int? limit, AppDbContext db) =>
{
    var take = Math.Clamp(limit ?? 100, 1, 500);
    var query = db.Transactions.AsNoTracking();

    if (!string.IsNullOrWhiteSpace(search))
    {
        var pattern = $"%{EscapeLike(search.Trim())}%";
        query = query.Where(t => EF.Functions.ILike(t.Description, pattern));
    }

    if (!string.IsNullOrWhiteSpace(category))
    {
        var exact = EscapeLike(category.Trim());
        query = query.Where(t => EF.Functions.ILike(t.Category, exact));
    }

    var result = await query
        .OrderByDescending(t => t.CreatedAt)
        .Take(take)
        .Select(t => new TransactionDto(t.Id, t.Description, t.Amount, t.Type, t.Category, t.CreatedAt))
        .ToListAsync();

    return Results.Ok(result);
})
.WithName("GetTransactions");

app.MapPost("/wallet/transactions", async (CreateTransactionRequest request, AppDbContext db) =>
{
    var errors = new Dictionary<string, string[]>();

    var description = request.Description?.Trim();
    if (string.IsNullOrEmpty(description))
        errors["description"] = ["Description is required."];
    else if (description.Length > MaxDescriptionLength)
        errors["description"] = [$"Description must be at most {MaxDescriptionLength} characters."];

    var amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
    if (amount == 0)
        errors["amount"] = ["Amount must be non-zero (max 2 decimal places)."];
    else if (Math.Abs(amount) > MaxAmount)
        errors["amount"] = [$"Amount must not exceed {MaxAmount.ToString("N0", CultureInfo.InvariantCulture)}."];

    var isIncome = amount > 0;
    var allowed = isIncome ? incomeCategories : expenseCategories;
    var category = isIncome ? DefaultIncomeCategory : DefaultExpenseCategory;

    if (!string.IsNullOrWhiteSpace(request.Category))
    {
        var match = allowed.FirstOrDefault(c => c.Equals(request.Category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            errors["category"] = [$"Category must be one of: {string.Join(", ", allowed)}."];
        else
            category = match;
    }

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    await using var dbTx = await db.Database.BeginTransactionAsync();
    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({WalletLockKey})");

    if (!isIncome)
    {
        var balance = await db.Transactions.SumAsync(t => t.Amount);
        if (balance + amount < 0)
        {
            return Results.Problem(
                title: "Insufficient funds",
                detail: $"Your balance is {Money(balance)} EUR, but this expense is {Money(Math.Abs(amount))} EUR.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    var transaction = new TransactionEntity
    {
        Id = Guid.NewGuid(),
        Description = description!,
        Amount = amount,
        Type = isIncome ? "Income" : "Expense",
        Category = category,
        CreatedAt = DateTime.UtcNow
    };

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync();
    await dbTx.CommitAsync();

    var dto = new TransactionDto(transaction.Id, transaction.Description, transaction.Amount,
        transaction.Type, transaction.Category, transaction.CreatedAt);

    return Results.Created($"/wallet/transactions/{transaction.Id}", dto);
})
.WithName("CreateTransaction");

app.MapGet("/wallet/analytics", async (string? period, AppDbContext db) =>
{
    var now = DateTime.UtcNow;
    var (since, label) = period?.ToLowerInvariant() switch
    {
        "weekly" => (now.AddDays(-7), "weekly"),
        "yearly" => (new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc), "yearly"),
        _ => (new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc), "monthly")
    };

    var inPeriod = db.Transactions.AsNoTracking().Where(t => t.CreatedAt >= since);

    var totals = await inPeriod
        .GroupBy(_ => 1)
        .Select(g => new
        {
            Income = g.Sum(t => t.Amount > 0 ? t.Amount : 0m),
            Expense = g.Sum(t => t.Amount < 0 ? -t.Amount : 0m),
            ExpenseCount = g.Count(t => t.Amount < 0)
        })
        .FirstOrDefaultAsync();

    var totalIncome = totals?.Income ?? 0m;
    var totalExpense = totals?.Expense ?? 0m;

    var expenses = inPeriod.Where(t => t.Amount < 0);

    var byCategory = await expenses
        .GroupBy(t => t.Category)
        .Select(g => new { Category = g.Key, Total = g.Sum(t => -t.Amount) })
        .OrderByDescending(x => x.Total)
        .ToListAsync();

    var peak = await expenses
        .OrderBy(t => t.Amount)
        .Select(t => new { t.Description, Amount = -t.Amount, t.Category })
        .FirstOrDefaultAsync();

    decimal? spentRatio = totalIncome > 0 ? Math.Round(totalExpense / totalIncome * 100m, 1) : null;
    decimal? savingsRate = spentRatio is null ? null : Math.Max(0m, 100m - spentRatio.Value);
    var avgExpense = totals is { ExpenseCount: > 0 } ? Math.Round(totalExpense / totals.ExpenseCount, 2) : 0m;

    return Results.Ok(new
    {
        Period = label,
        Since = since,
        TotalIncome = totalIncome,
        TotalExpense = totalExpense,
        SpentRatioPercentage = spentRatio,
        SavingsRatePercentage = savingsRate,
        AverageExpense = avgExpense,
        PeakExpense = peak,
        TopExpenseCategories = byCategory.Select(c => new
        {
            c.Category,
            c.Total,
            Percentage = totalExpense > 0 ? Math.Round(c.Total / totalExpense * 100m, 1) : 0m
        }),
        IncomeCategories = incomeCategories,
        ExpenseCategories = expenseCategories
    });
})
.WithName("GetAnalytics");

app.MapGet("/wallet/export/csv", async (AppDbContext db) =>
{
    var rows = await db.Transactions.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();

    var csv = new StringBuilder();
    csv.AppendLine("Id,Description,Amount,Type,Category,CreatedAt");

    foreach (var t in rows)
    {
        csv.AppendLine(string.Join(",",
            t.Id,
            EscapeCsv(t.Description),
            t.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            t.Type,
            EscapeCsv(t.Category),
            t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
    }

    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    return Results.File(bytes, "text/csv", "wallet-transactions.csv");
})
.WithName("ExportCsv");

app.MapGet("/healthz", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));

app.MapGet("/ready", async (AppDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { Status = "Ready", Timestamp = DateTime.UtcNow })
        : Results.Json(new { Status = "DatabaseUnavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapFallbackToFile("index.html");

app.Run();


static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

static string EscapeLike(string value) =>
    value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

static string EscapeCsv(string? field)
{
    var value = field ?? string.Empty;
    if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        value = "'" + value;
    return $"\"{value.Replace("\"", "\"\"")}\"";
}

record CreateTransactionRequest(string? Description, decimal Amount, string? Category = null);
record TransactionDto(Guid Id, string Description, decimal Amount, string Type, string Category, DateTime CreatedAt);