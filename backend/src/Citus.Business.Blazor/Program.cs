using Citus.Business.Blazor.Components;
using Citus.Business.Blazor.Configuration;
using Citus.Business.Blazor.Services;
using Citus.Business.Blazor.State;
using Citus.Modules.UnitySearch.Blazor;
using Citus.Ui.Shared.Theme;
using Infrastructure.PostgreSQL;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAntDesign();
builder.Services.AddCitusTheme();
builder.Services.Configure<AppHostOptions>(builder.Configuration.GetSection(AppHostOptions.SectionName));
builder.Services.AddScoped<BusinessShellState>();
builder.Services.AddTransient<BusinessSessionHeaderHandler>();

var businessDbConnectionString =
    builder.Configuration["CITUS_ACCOUNTING_DB"] ??
    builder.Configuration.GetConnectionString("AccountingCore");

if (!string.IsNullOrWhiteSpace(businessDbConnectionString))
{
    builder.Services.AddSingleton(new PostgreSqlConnectionFactory(businessDbConnectionString));
    builder.Services.AddSingleton<BusinessNumberingClient>();
}

builder.Services.AddScoped<BusinessWriteFlowClient>();
builder.Services.AddHttpClient<BusinessAuthenticationClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
        client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
    });
builder.Services.AddHttpClient<BusinessSessionClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<AccountingHealthClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<TrialBalanceClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<IncomeStatementClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<BalanceSheetClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ArAgingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ApAgingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<AccountingDocumentReviewClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<JournalEntryReviewClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// UnitySearchPickerService — talks to /accounting/unity-search and the new
// /accounting/unitysearch/usage endpoint. The session header handler attaches
// X-Citus-Business-User-Id / X-Citus-Business-Active-Company-Id so the API
// can enforce company isolation server-side.
builder.Services.AddHttpClient<UnitySearchPickerService>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet(
    "/system/health",
    () => Results.Ok(new
    {
        status = "ok",
        service = "Citus.Business.Blazor",
        utc = DateTimeOffset.UtcNow
    }));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
