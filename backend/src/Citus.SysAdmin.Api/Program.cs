var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Citus.SysAdmin.Api",
    status = "ready-for-wiring",
    purpose = "system administration and maintenance control"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Citus.SysAdmin.Api",
    utc = DateTimeOffset.UtcNow
}));

app.Run();
