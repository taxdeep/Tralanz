using Citus.Accounting.Api;
using static Citus.Accounting.Api.AccountingEndpointHelpers;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using Citus.Accounting.Api.Initialization;
using Citus.Accounting.Api.Tasks;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.CoaTemplates;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Statements;
using Citus.Accounting.Application.Reconciliation;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Companies;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Invoices;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Accounting.Infrastructure.Statements;
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Application.Pricing;
using Citus.Modules.Inventory.Domain.Shared;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Accounts;
using Infrastructure.PostgreSQL.Uom;
using Infrastructure.PostgreSQL.BusinessAuth;
using Infrastructure.PostgreSQL.Banking;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.CompanyAccess;
using Infrastructure.PostgreSQL.AP.Bills;
using Infrastructure.PostgreSQL.AP.Expenses;
using Infrastructure.PostgreSQL.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.Counterparties;
using Infrastructure.PostgreSQL.Sales;
using Modules.AP.Bills;
using Modules.AP.Expenses;
using Modules.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Inventory;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.Tax;
using Infrastructure.PostgreSQL.UnitySearch;
using Infrastructure.PostgreSQL.UnityAi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Citus.Accounting.Api.Authorization;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.SessionContext;
using Npgsql;
using Modules.Company.FeatureManagement;
using Modules.Company.MultiBook;
using Modules.Company.MultiCurrency;
using System.Text;
using System.Threading.RateLimiting;
using JournalEntryNumberLookup = Engines.Numbering.JournalEntry.IJournalEntryNumberLookup;
using GlIJournalEntryLifecycleStore = Modules.GL.JournalEntry.IJournalEntryLifecycleStore;
using GlIJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow;
using GlJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.JournalEntryLifecycleWorkflow;

namespace Citus.Accounting.Api.Endpoints;

internal static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this RouteGroupBuilder accounting)
    {
        accounting.MapGet(
            "/reports/trial-balance",
            async ([AsParameters] TrialBalanceLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetTrialBalanceAsync(
                    new GetTrialBalanceQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                        query.IncludeZeroBalances),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapTrialBalanceReport(report));
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsView);

        accounting.MapGet(
            "/reports/trial-balance/export.csv",
            async ([AsParameters] TrialBalanceLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetTrialBalanceAsync(
                    new GetTrialBalanceQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                        query.IncludeZeroBalances),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                var file = ReportCsvExporter.ExportTrialBalance(MapTrialBalanceReport(report));
                return ToCsvFileResult(file);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsExport);

        accounting.MapGet(
            "/reports/income-statement",
            async ([AsParameters] IncomeStatementLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

                if (dateFrom > dateTo)
                {
                    return Results.BadRequest(new
                    {
                        message = "Income Statement date range is invalid. dateFrom must be on or before dateTo."
                    });
                }

                var report = await repository.GetIncomeStatementAsync(
                    new GetIncomeStatementQuery(
                        query.CompanyId,
                        dateFrom,
                        dateTo,
                        query.IncludeZeroBalances,
                        AccountingBasisExtensions.ParseBasis(query.Basis)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapIncomeStatementReport(report));
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsView);

        accounting.MapGet(
            "/reports/income-statement/export.csv",
            async ([AsParameters] IncomeStatementLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

                if (dateFrom > dateTo)
                {
                    return Results.BadRequest(new
                    {
                        message = "Income Statement date range is invalid. dateFrom must be on or before dateTo."
                    });
                }

                var report = await repository.GetIncomeStatementAsync(
                    new GetIncomeStatementQuery(
                        query.CompanyId,
                        dateFrom,
                        dateTo,
                        query.IncludeZeroBalances,
                        AccountingBasisExtensions.ParseBasis(query.Basis)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                var file = ReportCsvExporter.ExportIncomeStatement(MapIncomeStatementReport(report));
                return ToCsvFileResult(file);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsExport);

        accounting.MapGet(
            "/reports/balance-sheet",
            async ([AsParameters] BalanceSheetLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetBalanceSheetAsync(
                    new GetBalanceSheetQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                        query.IncludeZeroBalances),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapBalanceSheetReport(report));
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsView);

        accounting.MapGet(
            "/reports/balance-sheet/export.csv",
            async ([AsParameters] BalanceSheetLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetBalanceSheetAsync(
                    new GetBalanceSheetQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                        query.IncludeZeroBalances),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                var file = ReportCsvExporter.ExportBalanceSheet(MapBalanceSheetReport(report));
                return ToCsvFileResult(file);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsExport);

        // Journal report — every posted debit/credit line in a date range, grouped
        // by journal entry in the UI.
        accounting.MapGet(
            "/reports/journal",
            async ([AsParameters] JournalLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var dateTo = query.DateTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var dateFrom = query.DateFrom ?? new DateOnly(dateTo.Year, dateTo.Month, 1);

                var report = await repository.GetJournalReportAsync(
                    new GetJournalReportQuery(query.CompanyId, dateFrom, dateTo),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new { message = "The active company is not provisioned in the accounting core yet." });
                }

                return Results.Ok(new JournalReportSummary
                {
                    DateFrom = report.DateFrom,
                    DateTo = report.DateTo,
                    BaseCurrencyCode = report.BaseCurrencyCode,
                    EntryCount = report.EntryCount,
                    LineCount = report.LineCount,
                    TotalDebit = report.TotalDebit,
                    TotalCredit = report.TotalCredit,
                    IsBalanced = report.IsBalanced,
                    Lines = report.Lines.Select(line => new JournalReportLineSummary
                    {
                        InternalNumber = line.InternalNumber,
                        JournalEntryId = line.JournalEntryId,
                        SourceType = line.SourceType,
                        SourceId = line.SourceId,
                        ReferenceNumber = line.ReferenceNumber,
                        PostingDate = line.PostingDate,
                        PartyName = line.PartyName,
                        Description = line.Description,
                        AccountCode = line.AccountCode,
                        AccountName = line.AccountName,
                        Debit = line.Debit,
                        Credit = line.Credit
                    }).ToList()
                });
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsView);

        accounting.MapGet(
            "/reports/ar-aging",
            async ([AsParameters] ArAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetArAgingAsync(
                    new GetArAgingQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapArAgingReport(report));
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArAgingView);

        accounting.MapGet(
            "/reports/ar-aging/export.csv",
            async ([AsParameters] ArAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetArAgingAsync(
                    new GetArAgingQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                var file = ReportCsvExporter.ExportArAging(MapArAgingReport(report));
                return ToCsvFileResult(file);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsExport);

        accounting.MapGet(
            "/reports/ap-aging",
            async ([AsParameters] ApAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetApAgingAsync(
                    new GetApAgingQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapApAgingReport(report));
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApAgingView);

        accounting.MapGet(
            "/reports/ap-aging/export.csv",
            async ([AsParameters] ApAgingLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetApAgingAsync(
                    new GetApAgingQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                var file = ReportCsvExporter.ExportApAging(MapApAgingReport(report));
                return ToCsvFileResult(file);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ReportsExport);

        // Customer open-item statement PDF. Reuses the A/R aging report (filtered to
        // the picked customer) for the open items + totals, joins the customer's
        // contact + the company letterhead, and renders via QuestPDF.
        accounting.MapGet(
            "/reports/customer-statement/{customerId:guid}/pdf",
            async (
                Guid customerId,
                [AsParameters] ArAgingLookupQuery query,
                ICompanyProfileQuery companyProfileQuery,
                ICustomerStore customerStore,
                IAccountingReportRepository repository,
                IStatementPdfRenderer renderer,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new { message = "The active company is not provisioned in the accounting core yet." });
                }

                var customer = await customerStore.GetByIdAsync(query.CompanyId, customerId, cancellationToken);
                if (customer is null)
                {
                    return Results.NotFound(new { message = "Customer was not found." });
                }

                var aging = await repository.GetArAgingAsync(new GetArAgingQuery(query.CompanyId, asOfDate), cancellationToken);
                var balance = aging?.CustomerRows.FirstOrDefault(row => row.CustomerId == customerId);
                var baseCurrency = aging?.BaseCurrencyCode ?? company.BaseCurrencyCode;

                var model = StatementRenderModelBuilder.BuildForCustomer(company, customer, balance, asOfDate, baseCurrency);
                var pdf = renderer.Render(model);

                return Results.File(pdf, "application/pdf", $"customer-statement-{customer.EntityNumber}-{asOfDate:yyyy-MM-dd}.pdf");
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArAgingView);

        // Vendor open-item statement PDF (mirror of the customer statement).
        accounting.MapGet(
            "/reports/vendor-statement/{vendorId:guid}/pdf",
            async (
                Guid vendorId,
                [AsParameters] ApAgingLookupQuery query,
                ICompanyProfileQuery companyProfileQuery,
                IVendorStore vendorStore,
                IAccountingReportRepository repository,
                IStatementPdfRenderer renderer,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new { message = "The active company is not provisioned in the accounting core yet." });
                }

                var vendor = await vendorStore.GetByIdAsync(query.CompanyId, vendorId, cancellationToken);
                if (vendor is null)
                {
                    return Results.NotFound(new { message = "Vendor was not found." });
                }

                var aging = await repository.GetApAgingAsync(new GetApAgingQuery(query.CompanyId, asOfDate), cancellationToken);
                var balance = aging?.VendorRows.FirstOrDefault(row => row.VendorId == vendorId);
                var baseCurrency = aging?.BaseCurrencyCode ?? company.BaseCurrencyCode;

                var model = StatementRenderModelBuilder.BuildForVendor(company, vendor, balance, asOfDate, baseCurrency);
                var pdf = renderer.Render(model);

                return Results.File(pdf, "application/pdf", $"vendor-statement-{vendor.EntityNumber}-{asOfDate:yyyy-MM-dd}.pdf");
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApAgingView);

        // Email a customer statement (PDF attached). Recipient falls back to the
        // customer's email on file when ToEmail is blank. Reuses the invoice SMTP
        // sender (the email request is document-agnostic).
        accounting.MapPost(
            "/reports/customer-statement/{customerId:guid}/send",
            async (
                Guid customerId,
                [AsParameters] ArAgingLookupQuery query,
                StatementSendHttpRequest request,
                ICompanyProfileQuery companyProfileQuery,
                ICustomerStore customerStore,
                IAccountingReportRepository repository,
                IStatementPdfRenderer renderer,
                IInvoiceEmailSender emailSender,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new { message = "The active company is not provisioned in the accounting core yet." });
                }

                var customer = await customerStore.GetByIdAsync(query.CompanyId, customerId, cancellationToken);
                if (customer is null)
                {
                    return Results.NotFound(new { message = "Customer was not found." });
                }

                var toEmail = !string.IsNullOrWhiteSpace(request.ToEmail) ? request.ToEmail.Trim() : customer.Email?.Trim();
                if (string.IsNullOrWhiteSpace(toEmail) || !toEmail.Contains('@', StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { message = "No recipient email — the customer has no email on file. Enter one to send." });
                }

                var aging = await repository.GetArAgingAsync(new GetArAgingQuery(query.CompanyId, asOfDate), cancellationToken);
                var balance = aging?.CustomerRows.FirstOrDefault(row => row.CustomerId == customerId);
                var baseCurrency = aging?.BaseCurrencyCode ?? company.BaseCurrencyCode;

                var model = StatementRenderModelBuilder.BuildForCustomer(company, customer, balance, asOfDate, baseCurrency);
                var pdf = renderer.Render(model);
                var composition = StatementEmailComposer.Compose(model, request.Message);

                var emailRequest = new InvoiceEmailRequest(
                    ToEmail: toEmail,
                    ToDisplayName: customer.DisplayName,
                    CcEmails: SplitEmailList(request.Cc),
                    BccEmails: SplitEmailList(request.Bcc),
                    Subject: composition.Subject,
                    HtmlBody: composition.HtmlBody,
                    PlainTextBody: composition.PlainTextBody,
                    AttachmentFileName: $"customer-statement-{customer.EntityNumber}-{asOfDate:yyyy-MM-dd}.pdf",
                    AttachmentBytes: pdf);

                var sendResult = await emailSender.SendAsync(emailRequest, cancellationToken);
                if (!sendResult.Succeeded)
                {
                    return Results.UnprocessableEntity(new { succeeded = false, message = sendResult.ErrorMessage ?? "Email delivery failed." });
                }

                return Results.Ok(new { succeeded = true, toEmail });
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArAgingView).RequireRateLimiting("invoice-send");

        // Email a vendor statement (mirror of the customer statement send).
        accounting.MapPost(
            "/reports/vendor-statement/{vendorId:guid}/send",
            async (
                Guid vendorId,
                [AsParameters] ApAgingLookupQuery query,
                StatementSendHttpRequest request,
                ICompanyProfileQuery companyProfileQuery,
                IVendorStore vendorStore,
                IAccountingReportRepository repository,
                IStatementPdfRenderer renderer,
                IInvoiceEmailSender emailSender,
                CancellationToken cancellationToken) =>
            {
                var asOfDate = query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

                var company = await companyProfileQuery.GetByIdAsync(query.CompanyId, cancellationToken);
                if (company is null)
                {
                    return Results.NotFound(new { message = "The active company is not provisioned in the accounting core yet." });
                }

                var vendor = await vendorStore.GetByIdAsync(query.CompanyId, vendorId, cancellationToken);
                if (vendor is null)
                {
                    return Results.NotFound(new { message = "Vendor was not found." });
                }

                var toEmail = !string.IsNullOrWhiteSpace(request.ToEmail) ? request.ToEmail.Trim() : vendor.Email?.Trim();
                if (string.IsNullOrWhiteSpace(toEmail) || !toEmail.Contains('@', StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { message = "No recipient email — the vendor has no email on file. Enter one to send." });
                }

                var aging = await repository.GetApAgingAsync(new GetApAgingQuery(query.CompanyId, asOfDate), cancellationToken);
                var balance = aging?.VendorRows.FirstOrDefault(row => row.VendorId == vendorId);
                var baseCurrency = aging?.BaseCurrencyCode ?? company.BaseCurrencyCode;

                var model = StatementRenderModelBuilder.BuildForVendor(company, vendor, balance, asOfDate, baseCurrency);
                var pdf = renderer.Render(model);
                var composition = StatementEmailComposer.Compose(model, request.Message);

                var emailRequest = new InvoiceEmailRequest(
                    ToEmail: toEmail,
                    ToDisplayName: vendor.DisplayName,
                    CcEmails: SplitEmailList(request.Cc),
                    BccEmails: SplitEmailList(request.Bcc),
                    Subject: composition.Subject,
                    HtmlBody: composition.HtmlBody,
                    PlainTextBody: composition.PlainTextBody,
                    AttachmentFileName: $"vendor-statement-{vendor.EntityNumber}-{asOfDate:yyyy-MM-dd}.pdf",
                    AttachmentBytes: pdf);

                var sendResult = await emailSender.SendAsync(emailRequest, cancellationToken);
                if (!sendResult.Succeeded)
                {
                    return Results.UnprocessableEntity(new { succeeded = false, message = sendResult.ErrorMessage ?? "Email delivery failed." });
                }

                return Results.Ok(new { succeeded = true, toEmail });
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApAgingView).RequireRateLimiting("invoice-send");

        // ---------------------------------------------------------------------------
        // Sales Overview — Cash Flow band (10 past + current + 3 forecast months)
        // and Income Over Time (accrual-basis revenue chart). Both pull from the
        // same accounting tables already feeding the AR aging report; new endpoints
        // just layer monthly bucketing on top.
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/sales/cash-flow",
            async ([AsParameters] SalesCashFlowLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetSalesCashFlowAsync(
                    new GetSalesCashFlowQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapSalesCashFlowReport(report));
            });

        accounting.MapGet(
            "/sales/income-over-time",
            async ([AsParameters] IncomeOverTimeLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var to = query.ToDate ?? today;
                // Default window: trailing 12 months ending on the as-of month.
                var defaultFrom = new DateOnly(to.Year, to.Month, 1).AddMonths(-11);
                var from = query.FromDate ?? defaultFrom;

                var report = await repository.GetIncomeOverTimeAsync(
                    new GetIncomeOverTimeQuery(
                        query.CompanyId,
                        from,
                        to,
                        query.CompareToPreviousYear),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapIncomeOverTimeReport(report));
            });

        // ---------------------------------------------------------------------------
        // Expense Overview — Cash Outflow band (10 past + current + 3 forecast
        // months) and Expense Over Time (accrual-basis cost chart). Mirrors the
        // Sales Overview endpoints; sources are bills + expenses + pay_bills +
        // ap_open_items instead of invoices + receive_payments + ar_open_items.
        // ---------------------------------------------------------------------------
        accounting.MapGet(
            "/expense/cash-outflow",
            async ([AsParameters] ExpenseCashOutflowLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var report = await repository.GetExpenseCashOutflowAsync(
                    new GetExpenseCashOutflowQuery(
                        query.CompanyId,
                        query.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow)),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapExpenseCashOutflowReport(report));
            });

        accounting.MapGet(
            "/expense/over-time",
            async ([AsParameters] ExpenseOverTimeLookupQuery query, IAccountingReportRepository repository, CancellationToken cancellationToken) =>
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var to = query.ToDate ?? today;
                var defaultFrom = new DateOnly(to.Year, to.Month, 1).AddMonths(-11);
                var from = query.FromDate ?? defaultFrom;

                var report = await repository.GetExpenseOverTimeAsync(
                    new GetExpenseOverTimeQuery(
                        query.CompanyId,
                        from,
                        to,
                        query.CompareToPreviousYear),
                    cancellationToken);

                if (report is null)
                {
                    return Results.NotFound(new
                    {
                        message = "The active company is not provisioned in the accounting core yet."
                    });
                }

                return Results.Ok(MapExpenseOverTimeReport(report));
            });
    }

    private static TrialBalanceReportSummary MapTrialBalanceReport(TrialBalanceReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            AsOfDate = report.AsOfDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
            AccountCount = report.AccountCount,
            TotalBalanceDebit = report.TotalBalanceDebit,
            TotalBalanceCredit = report.TotalBalanceCredit,
            IsBalanced = report.IsBalanced,
            Rows = report.Rows
                .Select(
                    static row => new TrialBalanceAccountSummary
                    {
                        AccountId = row.AccountId,
                        EntityNumber = row.EntityNumber,
                        Code = row.Code,
                        Name = row.Name,
                        RootType = row.RootType,
                        DetailType = row.DetailType,
                        IsActive = row.IsActive,
                        IsSystem = row.IsSystem,
                        PostedDebitTotal = row.PostedDebitTotal,
                        PostedCreditTotal = row.PostedCreditTotal,
                        BalanceDebit = row.BalanceDebit,
                        BalanceCredit = row.BalanceCredit,
                        NetBalance = row.NetBalance,
                        BalanceSide = row.BalanceSide
                    })
                .ToArray()
        };

    private static IncomeStatementReportSummary MapIncomeStatementReport(IncomeStatementReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            DateFrom = report.DateFrom,
            DateTo = report.DateTo,
            BaseCurrencyCode = report.BaseCurrencyCode,
            IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
            AccountCount = report.AccountCount,
            TotalRevenue = report.TotalRevenue,
            TotalCostOfSales = report.TotalCostOfSales,
            GrossProfit = report.GrossProfit,
            TotalExpenses = report.TotalExpenses,
            NetIncome = report.NetIncome,
            RevenueRows = report.RevenueRows.Select(MapIncomeStatementRow).ToArray(),
            CostOfSalesRows = report.CostOfSalesRows.Select(MapIncomeStatementRow).ToArray(),
            ExpenseRows = report.ExpenseRows.Select(MapIncomeStatementRow).ToArray()
        };

    private static BalanceSheetReportSummary MapBalanceSheetReport(BalanceSheetReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            AsOfDate = report.AsOfDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            IncludeZeroBalanceAccounts = report.IncludeZeroBalanceAccounts,
            AccountCount = report.AccountCount,
            TotalAssets = report.TotalAssets,
            TotalLiabilities = report.TotalLiabilities,
            CurrentEarnings = report.CurrentEarnings,
            TotalEquity = report.TotalEquity,
            TotalLiabilitiesAndEquity = report.TotalLiabilitiesAndEquity,
            IsBalanced = report.IsBalanced,
            AssetRows = report.AssetRows.Select(MapBalanceSheetRow).ToArray(),
            LiabilityRows = report.LiabilityRows.Select(MapBalanceSheetRow).ToArray(),
            EquityRows = report.EquityRows.Select(MapBalanceSheetRow).ToArray()
        };

    private static ArAgingReportSummary MapArAgingReport(ArAgingReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            AsOfDate = report.AsOfDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            CustomerCount = report.CustomerCount,
            OpenItemCount = report.OpenItemCount,
            CurrentAmountBase = report.CurrentAmountBase,
            Days1To30AmountBase = report.Days1To30AmountBase,
            Days31To60AmountBase = report.Days31To60AmountBase,
            Days61To90AmountBase = report.Days61To90AmountBase,
            DaysOver90AmountBase = report.DaysOver90AmountBase,
            TotalOverdueAmountBase = report.TotalOverdueAmountBase,
            TotalOutstandingAmountBase = report.TotalOutstandingAmountBase,
            CustomerRows = report.CustomerRows.Select(MapArAgingCustomer).ToArray(),
            DetailRows = report.DetailRows.Select(MapArAgingRow).ToArray()
        };

    private static ApAgingReportSummary MapApAgingReport(ApAgingReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            AsOfDate = report.AsOfDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            VendorCount = report.VendorCount,
            OpenItemCount = report.OpenItemCount,
            CurrentAmountBase = report.CurrentAmountBase,
            Days1To30AmountBase = report.Days1To30AmountBase,
            Days31To60AmountBase = report.Days31To60AmountBase,
            Days61To90AmountBase = report.Days61To90AmountBase,
            DaysOver90AmountBase = report.DaysOver90AmountBase,
            TotalOverdueAmountBase = report.TotalOverdueAmountBase,
            TotalOutstandingAmountBase = report.TotalOutstandingAmountBase,
            VendorRows = report.VendorRows.Select(MapApAgingVendor).ToArray(),
            DetailRows = report.DetailRows.Select(MapApAgingRow).ToArray()
        };

    private static IncomeStatementAccountSummary MapIncomeStatementRow(IncomeStatementAccountAmount row) =>
        new()
        {
            AccountId = row.AccountId,
            EntityNumber = row.EntityNumber,
            Code = row.Code,
            Name = row.Name,
            RootType = row.RootType,
            DetailType = row.DetailType,
            IsActive = row.IsActive,
            IsSystem = row.IsSystem,
            PostedDebitTotal = row.PostedDebitTotal,
            PostedCreditTotal = row.PostedCreditTotal,
            DisplayAmount = row.DisplayAmount
        };

    private static BalanceSheetAccountSummary MapBalanceSheetRow(BalanceSheetAccountAmount row) =>
        new()
        {
            AccountId = row.AccountId,
            EntityNumber = row.EntityNumber,
            Code = row.Code,
            Name = row.Name,
            RootType = row.RootType,
            DetailType = row.DetailType,
            IsActive = row.IsActive,
            IsSystem = row.IsSystem,
            IsSynthetic = row.IsSynthetic,
            PostedDebitTotal = row.PostedDebitTotal,
            PostedCreditTotal = row.PostedCreditTotal,
            DisplayAmount = row.DisplayAmount
        };

    private static SalesCashFlowSummary MapSalesCashFlowReport(SalesCashFlowReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            AsOfDate = report.AsOfDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            Months = report.Months.Select(month => new SalesCashFlowMonthSummary
            {
                Year = month.Year,
                Month = month.Month,
                MonthStart = month.MonthStart,
                IsForecast = month.IsForecast,
                IsCurrent = month.IsCurrent,
                ReceivedAmountBase = month.ReceivedAmountBase,
                ForecastAmountBase = month.ForecastAmountBase,
            }).ToArray(),
        };

    private static IncomeOverTimeSummary MapIncomeOverTimeReport(IncomeOverTimeReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            FromDate = report.FromDate,
            ToDate = report.ToDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            CompareToPreviousYear = report.CompareToPreviousYear,
            Months = report.Months.Select(MapIncomeMonth).ToArray(),
            PreviousYearMonths = report.PreviousYearMonths.Select(MapIncomeMonth).ToArray(),
        };

    private static IncomeOverTimeMonthSummary MapIncomeMonth(IncomeOverTimeMonthBucket bucket) =>
        new()
        {
            Year = bucket.Year,
            Month = bucket.Month,
            MonthStart = bucket.MonthStart,
            AmountBase = bucket.AmountBase,
        };

    private static ExpenseCashOutflowSummary MapExpenseCashOutflowReport(ExpenseCashOutflowReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            AsOfDate = report.AsOfDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            Months = report.Months.Select(month => new ExpenseCashOutflowMonthSummary
            {
                Year = month.Year,
                Month = month.Month,
                MonthStart = month.MonthStart,
                IsForecast = month.IsForecast,
                IsCurrent = month.IsCurrent,
                PaidAmountBase = month.PaidAmountBase,
                ForecastAmountBase = month.ForecastAmountBase,
            }).ToArray(),
        };

    private static ExpenseOverTimeSummary MapExpenseOverTimeReport(ExpenseOverTimeReport report) =>
        new()
        {
            CompanyId = report.CompanyId,
            FromDate = report.FromDate,
            ToDate = report.ToDate,
            BaseCurrencyCode = report.BaseCurrencyCode,
            CompareToPreviousYear = report.CompareToPreviousYear,
            Months = report.Months.Select(MapExpenseMonth).ToArray(),
            PreviousYearMonths = report.PreviousYearMonths.Select(MapExpenseMonth).ToArray(),
        };

    private static ExpenseOverTimeMonthSummary MapExpenseMonth(ExpenseOverTimeMonthBucket bucket) =>
        new()
        {
            Year = bucket.Year,
            Month = bucket.Month,
            MonthStart = bucket.MonthStart,
            AmountBase = bucket.AmountBase,
        };

    private static ArAgingCustomerSummary MapArAgingCustomer(ArAgingCustomerBalance row) =>
        new()
        {
            CustomerId = row.CustomerId,
            CustomerEntityNumber = row.CustomerEntityNumber,
            CustomerDisplayName = row.CustomerDisplayName,
            CustomerIsActive = row.CustomerIsActive,
            OpenItemCount = row.OpenItemCount,
            OldestDueDate = row.OldestDueDate,
            CurrentAmountBase = row.CurrentAmountBase,
            Days1To30AmountBase = row.Days1To30AmountBase,
            Days31To60AmountBase = row.Days31To60AmountBase,
            Days61To90AmountBase = row.Days61To90AmountBase,
            DaysOver90AmountBase = row.DaysOver90AmountBase,
            TotalOverdueAmountBase = row.TotalOverdueAmountBase,
            TotalOutstandingAmountBase = row.TotalOutstandingAmountBase,
            OpenItems = row.OpenItems.Select(MapArAgingRow).ToArray()
        };

    private static ArAgingOpenItemSummary MapArAgingRow(ArAgingOpenItemAmount row) =>
        new()
        {
            OpenItemId = row.OpenItemId,
            CustomerId = row.CustomerId,
            CustomerEntityNumber = row.CustomerEntityNumber,
            CustomerDisplayName = row.CustomerDisplayName,
            CustomerIsActive = row.CustomerIsActive,
            SourceType = row.SourceType,
            SourceDocumentId = row.SourceDocumentId,
            DisplayNumber = row.DisplayNumber,
            DocumentDate = row.DocumentDate,
            DueDate = row.DueDate,
            DaysPastDue = row.DaysPastDue,
            AgingBucket = row.AgingBucket,
            DocumentCurrencyCode = row.DocumentCurrencyCode,
            BaseCurrencyCode = row.BaseCurrencyCode,
            BalanceSide = row.BalanceSide,
            Status = row.Status,
            OriginalAmountTx = row.OriginalAmountTx,
            OriginalAmountBase = row.OriginalAmountBase,
            OpenAmountTx = row.OpenAmountTx,
            OpenAmountBase = row.OpenAmountBase,
            SignedOpenAmountTx = row.SignedOpenAmountTx,
            SignedOpenAmountBase = row.SignedOpenAmountBase
        };

    private static ApAgingVendorSummary MapApAgingVendor(ApAgingVendorBalance row) =>
        new()
        {
            VendorId = row.VendorId,
            VendorEntityNumber = row.VendorEntityNumber,
            VendorDisplayName = row.VendorDisplayName,
            VendorIsActive = row.VendorIsActive,
            OpenItemCount = row.OpenItemCount,
            OldestDueDate = row.OldestDueDate,
            CurrentAmountBase = row.CurrentAmountBase,
            Days1To30AmountBase = row.Days1To30AmountBase,
            Days31To60AmountBase = row.Days31To60AmountBase,
            Days61To90AmountBase = row.Days61To90AmountBase,
            DaysOver90AmountBase = row.DaysOver90AmountBase,
            TotalOverdueAmountBase = row.TotalOverdueAmountBase,
            TotalOutstandingAmountBase = row.TotalOutstandingAmountBase,
            OpenItems = row.OpenItems.Select(MapApAgingRow).ToArray()
        };

    private static ApAgingOpenItemSummary MapApAgingRow(ApAgingOpenItemAmount row) =>
        new()
        {
            OpenItemId = row.OpenItemId,
            VendorId = row.VendorId,
            VendorEntityNumber = row.VendorEntityNumber,
            VendorDisplayName = row.VendorDisplayName,
            VendorIsActive = row.VendorIsActive,
            SourceType = row.SourceType,
            SourceDocumentId = row.SourceDocumentId,
            DisplayNumber = row.DisplayNumber,
            DocumentDate = row.DocumentDate,
            DueDate = row.DueDate,
            DaysPastDue = row.DaysPastDue,
            AgingBucket = row.AgingBucket,
            DocumentCurrencyCode = row.DocumentCurrencyCode,
            BaseCurrencyCode = row.BaseCurrencyCode,
            BalanceSide = row.BalanceSide,
            Status = row.Status,
            OriginalAmountTx = row.OriginalAmountTx,
            OriginalAmountBase = row.OriginalAmountBase,
            OpenAmountTx = row.OpenAmountTx,
            OpenAmountBase = row.OpenAmountBase,
            SignedOpenAmountTx = row.SignedOpenAmountTx,
            SignedOpenAmountBase = row.SignedOpenAmountBase
        };
}
