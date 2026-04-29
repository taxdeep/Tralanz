using Citus.SysAdmin.Blazor.Components;
using Citus.SysAdmin.Blazor.Configuration;
using Citus.SysAdmin.Blazor.Services;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Localization;
using Citus.Ui.Shared.Services;
using Citus.Ui.Shared.Theme;
using Microsoft.Extensions.Options;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<CitusToastService>();
builder.Services.AddCitusTheme();
builder.Services.AddCitusLocalization();
builder.Services.Configure<AppHostOptions>(builder.Configuration.GetSection(AppHostOptions.SectionName));
builder.Services.AddScoped<AppShellState>();
builder.Services.AddHttpClient<SysAdminAuthenticationClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<PlatformCoreClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<SysAdminHealthClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<AccountingHealthClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<SysAdminControlClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<PlatformRuntimeMetricsClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<SmtpConfigClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<AiProviderConfigClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.SysAdminApiBaseUrl, UriKind.Absolute);
    });

var app = builder.Build();
var hostOptions = app.Services.GetRequiredService<IOptions<AppHostOptions>>().Value;

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

if (AppHostOptions.HasPathBase(hostOptions.PathBase))
{
    app.UsePathBase(AppHostOptions.NormalizePathBase(hostOptions.PathBase));
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseCitusLocalization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet(
    "/system/health",
    () => Results.Ok(new
    {
        status = "ok",
        service = "Citus.SysAdmin.Blazor",
        utc = DateTimeOffset.UtcNow
    }));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
