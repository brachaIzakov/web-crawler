using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Crawler.Api.Tests;

/// <summary>
/// Hosts the real API in-process with SQLite in-memory instead of Postgres, no background services
/// (nothing talks to RabbitMQ) and a substitute <see cref="IJobPublisher"/>.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ApiFactory() => _connection.Open();

    public IJobPublisher Publisher { get; } = Substitute.For<IJobPublisher>();

    /// <summary>Per-client POST /api/jobs limit; high by default so endpoint tests never hit it.</summary>
    protected virtual int CreateJobPerMinute => 100_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimits:CreateJobPerMinute", CreateJobPerMinute.ToString());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<CrawlerDbContext>));
            services.RemoveAll(typeof(DbContextOptions));
            services.AddDbContext<CrawlerDbContext>(options => options.UseSqlite(_connection));

            // Drop every background service except the one that runs the web server itself.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
            {
                if (descriptor.ImplementationType?.Name != "GenericWebHostService")
                    services.Remove(descriptor);
            }

            services.RemoveAll(typeof(IJobPublisher));
            services.AddSingleton(Publisher);
        });
    }

    /// <summary>Clears all rows and publisher calls so a class fixture can be shared between tests.</summary>
    public async Task ResetAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        await db.Edges.ExecuteDeleteAsync();
        // Self-referencing FK (NoAction since the lease migration): clear parents first.
        await db.Pages.ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentPageId, (long?)null));
        await db.Pages.ExecuteDeleteAsync();
        await db.Jobs.ExecuteDeleteAsync();
        Publisher.ClearSubstitute();
    }

    public async Task<T> WithDbAsync<T>(Func<CrawlerDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        return await action(db);
    }

    public Task WithDbAsync(Func<CrawlerDbContext, Task> action)
        => WithDbAsync(async db => { await action(db); return true; });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll(this IServiceCollection services, Type serviceType)
    {
        foreach (var d in services.Where(d => d.ServiceType == serviceType).ToList())
            services.Remove(d);
    }
}

internal static class HttpTestExtensions
{
    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())
           ?? throw new InvalidOperationException("Empty JSON body");

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body)
        => client.PostAsJsonAsync(url, body);

    public static string[] Keys(this JsonNode? node)
        => node is JsonObject obj ? obj.Select(kv => kv.Key).ToArray() : Array.Empty<string>();
}
