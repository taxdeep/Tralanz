using Connectors.FX.Frankfurter;
using Engines.FX.FxRateLookup;
using Engines.Numbering.JournalEntry;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.FX;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Modules.GL.JournalEntry;
using MudBlazor.Services;
using Web.Business.GL.JournalEntry;
using Web.Business.Reports.ReportType;
using Web.Shell;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var connectionString =
    builder.Configuration.GetConnectionString("AccountingCore")
    ?? Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
    ?? throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");

builder.Services.AddSingleton(new PostgreSqlConnectionFactory(connectionString));
builder.Services.AddSingleton<IFxRateStore, PostgreSqlFxRateStore>();
builder.Services.AddSingleton<IFxRateResolver, FxRateResolver>();
builder.Services.AddSingleton<IFxRateSelectionService, FxRateSelectionService>();
builder.Services.AddSingleton<IJournalEntryNumberLookup, PostgreSqlJournalEntryNumberLookup>();
builder.Services.AddSingleton<IJournalEntryAccountCatalog, PostgreSqlJournalEntryAccountCatalog>();
builder.Services.AddSingleton<IJournalEntryDraftStore, PostgreSqlJournalEntryDraftStore>();
builder.Services.AddSingleton<IJournalEntryPostingStore, PostgreSqlJournalEntryPostingStore>();
builder.Services.AddSingleton<IJournalEntryReviewStore, PostgreSqlJournalEntryReviewStore>();
builder.Services.AddSingleton<IManualJournalSourceReviewStore, PostgreSqlManualJournalSourceReviewStore>();
builder.Services.AddSingleton<IJournalEntryLifecycleStore, PostgreSqlJournalEntryLifecycleStore>();
builder.Services.AddSingleton<IJournalEntryLifecycleWorkflow, JournalEntryLifecycleWorkflow>();
builder.Services.AddSingleton<IJournalEntryWorkflow, JournalEntryWorkflow>();
builder.Services.AddHttpClient<IFxProviderClient, FrankfurterRatesClient>(client =>
{
    client.BaseAddress = new Uri("https://api.frankfurter.dev");
});

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Web.Shell",
    utc = DateTimeOffset.UtcNow
}));

app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(
        typeof(JournalEntryEditorPage).Assembly,
        typeof(ReportTypeSelectorState).Assembly)
    .AddInteractiveServerRenderMode();

app.Run();
