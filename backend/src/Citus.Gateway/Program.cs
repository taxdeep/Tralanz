using Yarp.ReverseProxy;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/app/"));
app.MapGet(
    "/health",
    () => Results.Ok(new
    {
        status = "ok",
        service = "Citus.Gateway",
        utc = DateTimeOffset.UtcNow
    }));
app.MapReverseProxy();

app.Run();
