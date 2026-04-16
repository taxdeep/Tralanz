using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Citus.Platform.Core.Services;
using Citus.Platform.Infrastructure.Notifications;
using Citus.Platform.Infrastructure.Persistence;
using Connectors.FX.Frankfurter;
using Engines.FX.FxRateLookup;
using Engines.Numbering.JournalEntry;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP;
using Infrastructure.PostgreSQL.AR;
using Infrastructure.PostgreSQL.CompanyAccess;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.FX;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Numbering;
using Modules.AP.PayBill;
using Modules.AP.VendorCreditApplication;
using Modules.AP.VendorCurrency;
using Modules.AR.CreditApplication;
using Modules.AR.CustomerCurrency;
using Modules.AR.ReceivePayment;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.SessionContext;
using Modules.CompanyAccess.SystemSetup;
using Modules.Company.MultiBook;
using Modules.Company.MultiCurrency;
using Modules.GL.JournalEntry;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Web.Shell.Adapters;
using Web.Shell.Configuration;
using Web.Shell.Services;
using Web.Shell.State;
using Web.Business.AP.PayBill;
using Web.Business.AP.SettlementLookup;
using Web.Business.AP.SettlementPosting;
using Web.Business.AP.VendorCreditApplication;
using Web.Business.AR.CreditApplication;
using Web.Business.AR.SettlementLookup;
using Web.Business.AR.SettlementPosting;
using Web.Business.AR.ReceivePayment;
using Web.Business.CompanyAccess.Memberships;
using Web.Business.GL.JournalEntry;
using Web.Business.Reports.ReportType;
using Web.Shell;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.Configure<WebShellAppHostOptions>(builder.Configuration.GetSection(WebShellAppHostOptions.SectionName));
builder.Services.Configure<PlatformEmailDeliveryOptions>(builder.Configuration.GetSection(PlatformEmailDeliveryOptions.SectionName));
builder.Services.AddScoped<WebShellState>();
builder.Services.AddScoped<WebShellSessionExpirationCoordinator>();
builder.Services.AddScoped<ICompanyAccessShellSession, WebShellCompanyAccessShellSession>();
builder.Services.AddTransient<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<PlatformProfileClient>(
    (serviceProvider, client) =>
    {
        var navigationManager = serviceProvider.GetRequiredService<NavigationManager>();
        client.BaseAddress = new Uri(navigationManager.BaseUri);
    });
builder.Services.AddHttpClient<WebShellBusinessSessionClient>(
        (serviceProvider, client) =>
        {
            var navigationManager = serviceProvider.GetRequiredService<NavigationManager>();
            client.BaseAddress = new Uri(navigationManager.BaseUri);
        })
    ;
builder.Services.AddHttpClient(
        "AccountingApi",
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ShellAccountingDocumentReviewClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ShellAccountingDocumentBrowserClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ShellOpenItemDrillDownClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ShellOpenItemAdjustmentAccountMappingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ShellSourceDocumentDraftClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ArSettlementPostingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ApSettlementPostingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ShellSettlementPostingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl);
        })
    .AddHttpMessageHandler<WebShellBusinessSessionHeaderHandler>();

var connectionString =
    builder.Configuration.GetConnectionString("AccountingCore")
    ?? Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
    ?? throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");

builder.Services.AddSingleton(new PostgreSqlConnectionFactory(connectionString));
builder.Services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
builder.Services.AddSingleton<SysAdminPasswordHasher>();
builder.Services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
builder.Services.AddSingleton<IPlatformBusinessSessionRepository, PostgresPlatformBusinessSessionRepository>();
builder.Services.AddSingleton<IPlatformVerificationNotificationSender, SmtpPlatformVerificationNotificationSender>();
builder.Services.AddSingleton<IPlatformAccountProfileRepository, PostgresPlatformAccountProfileRepository>();
builder.Services.AddSingleton<IPlatformAccountProfileWorkflow, PlatformAccountProfileWorkflow>();
builder.Services.AddSingleton<IPlatformNotificationReadinessWorkflow, PlatformNotificationReadinessWorkflow>();
builder.Services.AddSingleton<IFxRateStore, PostgreSqlFxRateStore>();
builder.Services.AddSingleton<IFxRateResolver, FxRateResolver>();
builder.Services.AddSingleton<IFxRateSelectionService, FxRateSelectionService>();
builder.Services.AddSingleton<ICompanyCurrencyProvisioningStore, PostgreSqlCompanyCurrencyProvisioningStore>();
builder.Services.AddSingleton<ICompanyCurrencyCatalog>(provider =>
    provider.GetRequiredService<ICompanyCurrencyProvisioningStore>());
builder.Services.AddSingleton<ICompanyCurrencyGovernanceWorkflow, CompanyCurrencyGovernanceWorkflow>();
builder.Services.AddSingleton<ICompanyBookPolicyStore, PostgreSqlCompanyBookPolicyStore>();
builder.Services.AddSingleton<ICompanyBookPolicyWorkflow, CompanyBookPolicyWorkflow>();
builder.Services.AddSingleton<ICompanySessionContextStore, PostgreSqlCompanySessionContextStore>();
builder.Services.AddSingleton<ICompanySessionContextWorkflow, CompanySessionContextWorkflow>();
builder.Services.AddSingleton<ICompanyMembershipPermissionStore, PostgreSqlCompanyMembershipPermissionStore>();
builder.Services.AddSingleton<ICompanyMembershipPermissionWorkflow, CompanyMembershipPermissionWorkflow>();
builder.Services.AddSingleton<ShellTaxCodeLookupService>();
builder.Services.AddSingleton<ICustomerCurrencyStore, PostgreSqlCustomerCurrencyStore>();
builder.Services.AddSingleton<ICustomerCurrencyWorkflow, CustomerCurrencyWorkflow>();
builder.Services.AddSingleton<IReceivePaymentDraftPreparationStore, PostgreSqlReceivePaymentDraftPreparationStore>();
builder.Services.AddSingleton<IReceivePaymentDraftPreparationWorkflow, ReceivePaymentDraftPreparationWorkflow>();
builder.Services.AddSingleton<ICreditApplicationDraftPreparationStore, PostgreSqlCreditApplicationDraftPreparationStore>();
builder.Services.AddSingleton<ICreditApplicationDraftPreparationWorkflow, CreditApplicationDraftPreparationWorkflow>();
builder.Services.AddSingleton<IArSettlementLookupService, ArSettlementLookupService>();
builder.Services.AddSingleton<IVendorCurrencyStore, PostgreSqlVendorCurrencyStore>();
builder.Services.AddSingleton<IVendorCurrencyWorkflow, VendorCurrencyWorkflow>();
builder.Services.AddSingleton<IPayBillDraftPreparationStore, PostgreSqlPayBillDraftPreparationStore>();
builder.Services.AddSingleton<IPayBillDraftPreparationWorkflow, PayBillDraftPreparationWorkflow>();
builder.Services.AddSingleton<IVendorCreditApplicationDraftPreparationStore, PostgreSqlVendorCreditApplicationDraftPreparationStore>();
builder.Services.AddSingleton<IVendorCreditApplicationDraftPreparationWorkflow, VendorCreditApplicationDraftPreparationWorkflow>();
builder.Services.AddSingleton<IApSettlementLookupService, ApSettlementLookupService>();
builder.Services.AddSingleton<ISystemSetupStore, PostgreSqlSystemSetupStore>();
builder.Services.AddSingleton<ISystemSetupWorkflow, SystemSetupWorkflow>();
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
var appHostOptions = app.Services.GetRequiredService<IOptions<WebShellAppHostOptions>>().Value;

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Web.Shell",
    utc = DateTimeOffset.UtcNow
}));

app.MapBusinessSessionApi();
app.MapPlatformProfileApi();
app.MapPlatformNotificationApi();

if (!appHostOptions.DisableRazorComponents)
{
    app.MapRazorComponents<App>()
        .AddAdditionalAssemblies(
            typeof(ReceivePaymentPage).Assembly,
            typeof(CreditApplicationPage).Assembly,
            typeof(PayBillPage).Assembly,
            typeof(VendorCreditApplicationPage).Assembly,
            typeof(JournalEntryEditorPage).Assembly,
            typeof(CompanyMembershipPermissionsPage).Assembly,
            typeof(ReportTypeSelectorState).Assembly)
        .AddInteractiveServerRenderMode();
}

app.Run();
