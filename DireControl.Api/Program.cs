var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddOpenApi();

var app = builder.Build();

app
    .UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    })
    .MapOpenApi()
    .UseHttpsRedirection()
    .UseRouting()
    .UseAuthorization()
    .MapControllers()
    .UseDefaultFiles()
    .UseStaticFiles()
    .MapFallbackToFile("index.html");
await app.RunAsync();
