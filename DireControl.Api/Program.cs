using DireControl.Api;
using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

var services = builder.Services;
var config = builder.Configuration;

services.Configure<DireControlOptions>(config.GetSection(DireControlOptions.Section));
services.Configure<DirewolfOptions>(config.GetSection(DirewolfOptions.Section));
services.Configure<QrzOptions>(config.GetSection(QrzOptions.Section));

services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
    });
services.AddOpenApi();
services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.Converters.Add(new UtcDateTimeConverter());
    });
services.AddDbContext<DireControlContext>(options =>
    options.UseSqlite(config.GetConnectionString("Default") ?? "Data Source=direcontrol.db"));

var corsOrigins = config
    .GetSection($"{DireControlOptions.Section}:CorsOrigins")
    .Get<string[]>()
    ?? ["http://localhost:5173", "https://localhost:5173"];

services.AddCors(options =>
{
    options.AddPolicy("VueDevServer", policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

services.AddHttpClient("HamDB").ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
});
services.AddHttpClient("QRZ").ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("DireControl/1.0");
});

services.AddHostedService<KissTcpService>();
services.AddHostedService<AprsPacketParsingService>();
services.AddHostedService<StationExpiryService>();
services.AddHostedService<AlertingService>();

services.AddSingleton<KissConnectionHolder>();
services.AddSingleton<MessageSendingService>();
services.AddSingleton<PendingAlertChannel>();
services.AddSingleton<CallsignLookupService>();
services.AddSingleton<StatisticsService>();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("VueDevServer");
app.UseAuthorization();

app.MapControllers();
app.MapHub<PacketHub>(PacketHub.HubPath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
