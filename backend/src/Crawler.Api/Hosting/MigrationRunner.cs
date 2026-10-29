using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Hosting;

public static class MigrationRunner
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Applies pending EF Core migrations, retrying while the database is still starting
    /// (e.g. Postgres container not yet accepting connections under docker compose).
    /// </summary>
    // Arbitrary but fixed key: every API replica serializes on it, so concurrent start-ups never race migrations.
    private const long MigrationLockKey = 0x43524157_4C455231; // "CRAWLER1"

    private static async Task MigrateUnderAdvisoryLockAsync(CrawlerDbContext db, CancellationToken ct)
    {
        // The session lock lives on this connection, so keep it open for the whole migration.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrationLockKey})", ct);
            try
            {
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrationLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MigrationRunner));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();

                // Migrations are generated for Postgres; other providers (SQLite in the API tests) get the model directly.
                if (db.Database.IsRelational() && !db.Database.IsNpgsql())
                {
                    await db.Database.EnsureCreatedAsync(app.Lifetime.ApplicationStopping);
                    logger.LogInformation("Database schema created (non-Postgres provider)");
                    return;
                }

                await MigrateUnderAdvisoryLockAsync(db, app.Lifetime.ApplicationStopping);
                logger.LogInformation("Database migrations applied");
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/{MaxAttempts}): {Error}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(RetryDelay, app.Lifetime.ApplicationStopping);
            }
        }
    }
}
