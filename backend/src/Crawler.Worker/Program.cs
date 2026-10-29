using System.Text;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Logging;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using Crawler.Worker.Health;
using Crawler.Worker.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Polly;

const string LiveTag = "live";
const string ReadyTag = "ready";

// windows-1252, shift_jis, gb2312, ... are only available through the code pages provider.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseCrawlerSerilog("worker");

var configuration = builder.Configuration;
var postgresConnectionString = configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var rabbitMq = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
var crawlSection = configuration.GetSection(CrawlOptions.SectionName);
// The HTTP handler and resilience pipeline are built from the startup values (ValidateOnStart checks the same section).
var crawl = crawlSection.Get<CrawlOptions>() ?? new CrawlOptions();

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddCrawlerPersistence(configuration);
builder.Services.AddRabbitMqOptions(configuration);
builder.Services.AddOptions<RabbitMqOptions>()
    .Validate(o => o.PrefetchCount >= 1 && o.MaxRetries >= 0 && o.RetryBaseDelaySeconds >= 0,
        "RabbitMq: PrefetchCount must be >= 1, MaxRetries and RetryBaseDelaySeconds >= 0.")
    .ValidateOnStart();
builder.Services.AddOptions<CrawlOptions>()
    .Bind(crawlSection)
    .Validate(o => o.IsValid(), "Crawl options are invalid (see CrawlOptions.IsValid).")
    .ValidateOnStart();
builder.Services.AddSingleton<WorkerIdentity>();

builder.Services
    .AddHttpClient<IPageFetcher, HttpPageFetcher>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(crawl.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
    })
    .ConfigurePrimaryHttpMessageHandler(() => HttpPageFetcher.CreateHandler(crawl))
    // Retries 5xx/408/429, HttpRequestException and attempt timeouts with exponential backoff + jitter.
    // One pipeline (and circuit breaker) per authority, so a failing host cannot open the breaker for everyone.
    .AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(crawl.Http.TotalRequestTimeoutSeconds);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(crawl.Http.AttemptTimeoutSeconds);
        options.Retry.MaxRetryAttempts = crawl.Http.MaxRetries;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome));
    })
    .SelectPipelineByAuthority();

builder.Services.AddSingleton<IJobProgressNotifier, RabbitMqJobProgressNotifier>();
// Optional ctor parameters (TimeProvider, IJobProgressNotifier, WorkerIdentity) are injected because they are registered.
builder.Services.AddScoped<CrawlEngine>();
builder.Services.AddSingleton<CrawlJobConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CrawlJobConsumer>());

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag])
    .AddNpgSql(postgresConnectionString, name: "postgres", tags: [ReadyTag])
    .AddRabbitMQ(rabbitConnectionString: rabbitMq.AmqpUri, name: "rabbitmq", tags: [ReadyTag])
    .AddCheck<RabbitMqConsumerHealthCheck>("consumer", tags: [ReadyTag]);

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => check.Tags.Contains(LiveTag) });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

app.Run();

// A blocked address will be blocked again; retrying it only wastes time.
static bool IsRetryable(Outcome<HttpResponseMessage> outcome)
    => outcome.Exception is not BlockedAddressException and not { InnerException: BlockedAddressException }
       && HttpClientResiliencePredicates.IsTransient(outcome);
