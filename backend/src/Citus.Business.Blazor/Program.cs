using Citus.Business.Blazor.Components;
using Citus.Business.Blazor.Configuration;
using Citus.Business.Blazor.Services;
using Citus.Business.Blazor.State;
using Citus.Modules.UnitySearch.Blazor;
using Citus.Ui.Shared.Services;
using Citus.Ui.Shared.Theme;
using Infrastructure.PostgreSQL;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Options;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<CitusToastService>();
builder.Services.AddCitusTheme();
builder.Services.Configure<AppHostOptions>(builder.Configuration.GetSection(AppHostOptions.SectionName));
builder.Services.Configure<FeatureFlagsOptions>(builder.Configuration.GetSection(FeatureFlagsOptions.SectionName));
builder.Services.AddScoped<BusinessShellState>();

// Bridge the circuit's IServiceProvider into HttpClientFactory's handler
// pool. Without this, BusinessSessionHeaderHandler captures a default-
// constructed BusinessShellState from the factory's own scope and every
// outgoing API call carries Guid.Empty for UserId / ActiveCompanyId.
// See CircuitServicesAccessor.cs for the longer explanation.
builder.Services.AddSingleton<CircuitServicesAccessor>();
builder.Services.AddScoped<CircuitHandler, CircuitServicesAccessorCircuitHandler>();
builder.Services.AddTransient<BusinessSessionHeaderHandler>();

var businessDbConnectionString =
    builder.Configuration["CITUS_ACCOUNTING_DB"] ??
    builder.Configuration.GetConnectionString("AccountingCore");

if (!string.IsNullOrWhiteSpace(businessDbConnectionString))
{
    builder.Services.AddSingleton(new PostgreSqlConnectionFactory(businessDbConnectionString));
    builder.Services.AddSingleton<BusinessNumberingClient>();
}

// BusinessWriteFlowClient now talks to /accounting/manual-journals/save-and-post
// directly, so it gets a typed HttpClient with the same base URL + business
// session header handler used by every other accounting client. Keeping the
// scoped lifetime — it caches no per-request state.
builder.Services.AddHttpClient<BusinessWriteFlowClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
// AUDIT_2026-05-25: attach the BusinessSessionHeaderHandler. SwitchActiveCompanyAsync
// and other authenticated endpoints on this client (profile read/update, switch-active-
// company) rely on the standard X-Citus-Business-Session-Token header being attached
// automatically from BusinessShellState. SignInAsync runs anonymously (empty token →
// handler no-ops), and ResumeSession/SignOut already attach the header manually with
// the just-captured token, so adding the handler is safe for every method on this client.
builder.Services.AddHttpClient<BusinessAuthenticationClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
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
// H17: AI-generated dashboard suggestions for the operator dashboard.
builder.Services.AddHttpClient<DashboardSuggestionsClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<SalesOverviewClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<ExpenseOverviewClient>(
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
builder.Services.AddHttpClient<InvoiceTemplateClient>(
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

builder.Services.AddHttpClient<TaxCodeClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<AccountClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// UOM master (read-only V1) — backs the Item edit dropdown + drives the
// qty input step on Task / Invoice / Bill line grids.
builder.Services.AddHttpClient<UomClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<BankReconciliationClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// Receive Payment page: lists the customer's open invoices (and, once
// Commit B brings them in, their existing customer deposits as negative
// rows) so the operator can tick which ones the cash applies to.
builder.Services.AddHttpClient<OpenReceivablesClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<CompanyCurrencyClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<FxRateClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<CustomerClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// "+ New Company" on the My Companies page. Same header handler so
// the BusinessSession token + active-company id ride along — the
// active-company id isn't read by the endpoint but the auth filter
// expects it on every business-shell request.
builder.Services.AddHttpClient<CompanyProvisioningClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// Customer detail page aggregates: financial-summary + transactions
// timeline. Reuses the same business-session header handler the rest
// of the per-customer surfaces use.
builder.Services.AddHttpClient<CustomerOverviewClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// Persisted shipping address book CRUD for the Profile tab. The
// historical-address picker (CustomerClient.ListShippingAddressHistoryAsync)
// stays intact for now; both surfaces will eventually feed the
// AddressEditor's "Use a previous address" dropdown.
builder.Services.AddHttpClient<CustomerShippingAddressBookClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// AP-side mirror clients for the Vendor detail page: financial summary
// + transactions timeline, plus the persisted shipping address book.
builder.Services.AddHttpClient<VendorOverviewClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<VendorShippingAddressBookClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<VendorClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<ItemClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// Batch 6: per-company module-flag fetch + Task module HTTP client.
// Both ride the same business-session header handler so they
// authenticate as the active company.
builder.Services.AddHttpClient<ModuleFlagsClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<TaskClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<TaskMarginReportClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<TaskRelatedDocumentsClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<InventoryActivationClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<WarehouseClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<SalesIssueCogsClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<CustomerDepositClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<DropShipClearingClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<AccountingPeriodClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// PR-4F: Owner-only permissions management page consumes this client
// for all five surfaces (list members, registry, snapshot, grant,
// revoke).
builder.Services.AddHttpClient<PermissionManagementClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<YearEndPreCloseClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<AuditLogClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<PaymentTermClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<QuoteClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<SalesOrderClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<BillClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<InvoiceClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

// V1 document read clients — one per doc-type for list + detail
// surfaces. The write side runs through BusinessWriteFlowClient.
builder.Services.AddHttpClient<SalesReceiptClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<RefundReceiptClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<CreditMemoClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<VendorCreditClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<BankTransferClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<BankDepositClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();
builder.Services.AddHttpClient<TaxReturnClient>(
        (sp, c) => { c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AppHostOptions>>().Value.AccountingApiBaseUrl, UriKind.Absolute); })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<PurchaseOrderClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppHostOptions>>().Value;
            client.BaseAddress = new Uri(options.AccountingApiBaseUrl, UriKind.Absolute);
        })
    .AddHttpMessageHandler<BusinessSessionHeaderHandler>();

builder.Services.AddHttpClient<ExpenseClient>(
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
