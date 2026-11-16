using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Api;

/// Creates the database schema in the background (so the web server starts listening at once),
/// but remembers whether it succeeded, so /ready can report the truth to Kubernetes.
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private const int MaxRetries = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private volatile bool _isReady;

    public bool IsReady => _isReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                logger.LogInformation("Initializing the database (attempt {Attempt}/{Max})...", attempt, MaxRetries);
                await db.Database.EnsureCreatedAsync(stoppingToken);

                _isReady = true;
                logger.LogInformation("Database connected and initialized successfully.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(ex, "Database not ready yet. Retrying in {Seconds} seconds...", RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (Exception ex)
            {
                // Give up loudly: the pod exits and Kubernetes restarts it, instead of
                // pretending to be healthy forever with no database.
                logger.LogCritical(ex, "Could not initialize the database after {Max} attempts. Stopping the application.", MaxRetries);
                lifetime.StopApplication();
                return;
            }
        }
    }
}
