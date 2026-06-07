using DireControl.Api;
using DireControl.Api.Hubs;
using DireControl.Api.Logging;
using DireControl.Api.Services;
using DireControl.Api.Services.Weather;
using DireControl.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

config.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Runtime, DB-backed log-level overrides. Added last so they take precedence over
// appsettings; updating them at runtime reloads the logging filters live.
var runtimeLoggingSource = new RuntimeLoggingConfigSource();
((IConfigurationBuilder)config).Add(runtimeLoggingSource);

services
    .AddSingleton(runtimeLoggingSource)
    .AddSingleton<LogLevelService>()
    .Configure<DireControlOptions>(config.GetSection(DireControlOptions.Section))
    .Configure<DirewolfOptions>(config.GetSection(DirewolfOptions.Section))
    .Configure<QrzOptions>(config.GetSection(QrzOptions.Section))
    .AddOpenApi()
    .AddDbContext<DireControlContext>(options =>
        options.UseSqlite(config.GetConnectionString("Default") ?? "Data Source=direcontrol.db"))
    .AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        })
        .Services
    .AddSignalR()
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        })
        .Services
    .AddHttpClient("HamDB")
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
        })
        .Services
    .AddHttpClient("QRZ")
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
        })
        .Services
    .AddHttpClient("RainViewer")
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
        })
        .Services
    .AddHttpClient("OpenWeatherMap")
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
        })
        .Services
    .AddHttpClient("TomorrowIo")
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
        })
        .Services
    .AddHttpClient("IEM")
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
        })
        .Services
    .AddHostedService<KissTcpService>()
    .AddHostedService<AprsPacketParsingService>()
    .AddHostedService<AprsIsService>()
    .AddHostedService<StationExpiryService>()
    .AddHostedService<AlertingService>()
    .AddHostedService<MessageRetryService>()
    .AddHostedService<WeatherCacheService>()
    .AddHostedService<StatisticsAggregationService>()
    .AddHostedService<LogBroadcastService>()
    .AddHostedService<DatabaseCleanupHostedService>()
    .AddSingleton<DatabaseMaintenanceService>()
    .AddSingleton<LogStreamBroadcaster>()
    .AddSingleton<ILoggerProvider, SignalRLoggerProvider>()
    .AddSingleton<KissConnectionHolder>()
    .AddSingleton<AprsIsReconnectTrigger>()
    .AddSingleton<IAprsIsStatusService, AprsIsStatusService>()
    .AddSingleton<BeaconService>()
    .AddSingleton<MessageSendingService>()
    .AddSingleton<PendingAlertChannel>()
    .AddSingleton<CallsignLookupService>()
    .AddSingleton<StatisticsService>()
    .AddSingleton<RainViewerRadarProvider>()
    .AddSingleton<IemRadarProvider>()
    .AddSingleton<WindTileCache>()
    .AddSingleton<LightningCache>();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);
await app.Services.GetRequiredService<LogLevelService>().ApplyFromDatabaseAsync();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
})
.UseHttpsRedirection()
.UseRouting()
.UseAuthorization()
.UseDefaultFiles()
.UseStaticFiles();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.MapHub<PacketHub>(PacketHub.HubPath);
app.MapHub<LogHub>(LogHub.HubPath);
app.MapFallbackToFile("index.html");

await app.RunAsync();
