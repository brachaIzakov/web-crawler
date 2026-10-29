using System.Text.Json.Serialization;
using Crawler.Api.Endpoints;
using Crawler.Api.Hosting;
using Crawler.Api.Http;
using Crawler.Api.Services;
using Crawler.Api.Streaming;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Logging;
using Crawler.Infrastructure.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

const string CorsPolicy = "frontend";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseCrawlerSerilog("api");

builder.Services
    .AddCrawlerPersistence(builder.Configuration)
    .AddRabbitMqOptions(builder.Configuration)
    .AddJobPublisher()
    .AddSingleton(TimeProvider.System)
    .AddScoped<IJobService, JobService>();

builder.Services
    .AddSingleton<JobProgressHub>()
    .AddSingleton<JobEventStream>()
    .AddSingleton<JobEventsConsumer>()
    .AddHostedService(sp => sp.GetRequiredService<JobEventsConsumer>())
    .AddHostedService<JobReconciler>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCrawlerRateLimiting(builder.Configuration);
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    // Only needed for the Vite dev server; in compose nginx proxies /api same-origin.
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Contains("*"))
        policy.AllowAnyOrigin();
    else
        policy.WithOrigins(origins);
    policy.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "Location");
}));

var rabbitOptions = builder.Configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres")!, name: "postgres", tags: ["ready"])
    .AddRabbitMQ(new Uri(rabbitOptions.AmqpUri), name: "rabbitmq", tags: ["ready"])
    // Live updates degrade to polling without the events consumer, so it must not take the API out of rotation.
    .AddCheck<EventsConsumerHealthCheck>("events-consumer", failureStatus: HealthStatus.Degraded, tags: ["ready"]);

// nginx (and any other reverse proxy) sets X-Forwarded-For/Proto; without this every client behind the proxy
// would share one rate-limit bucket. Trusting all proxies is fine for the compose setup; lock KnownProxies down
// when deploying behind an untrusted network edge.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseResponseCompression();
app.UseCors(CorsPolicy);
app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthResponseWriter.WriteJson });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = HealthResponseWriter.WriteJson });
app.MapJobEndpoints();

await app.MigrateDatabaseAsync();
await app.RunAsync();

/// <summary>Makes the entry point visible to WebApplicationFactory in the API tests.</summary>
public partial class Program;
