using DireControl.Api;
using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

config.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

services
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
    .AddHostedService<KissTcpService>()
    .AddHostedService<AprsPacketParsingService>()
    .AddHostedService<StationExpiryService>()
    .AddHostedService<AlertingService>()
    .AddSingleton<KissConnectionHolder>()
    .AddSingleton<MessageSendingService>()
    .AddSingleton<PendingAlertChannel>()
    .AddSingleton<CallsignLookupService>()
    .AddSingleton<StatisticsService>();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

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
app.MapFallbackToFile("index.html");

await app.RunAsync();
