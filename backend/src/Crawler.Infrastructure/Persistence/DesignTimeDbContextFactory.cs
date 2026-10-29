using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crawler.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` tooling to generate migrations; no live DB is required.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CrawlerDbContext>
{
    public CrawlerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CrawlerDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=crawler;Username=crawler;Password=crawler")
            .Options;
        return new CrawlerDbContext(options);
    }
}
