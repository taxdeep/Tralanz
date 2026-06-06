using Citus.Accounting.Api;
using Citus.Accounting.Api.Endpoints;
using Citus.Accounting.Api.Startup;
using static Citus.Accounting.Api.AccountingEndpointHelpers;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using static Citus.Accounting.Api.Authorization.EndpointApprovalAuthorityHelpers;
using static Citus.Accounting.Api.Endpoints.Support.ReviewMappers;
using static Citus.Accounting.Api.Endpoints.Support.BusinessSessionEndpointHelpers;
using static Citus.Accounting.Api.Endpoints.Support.EndpointRequestHelpers;
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

/// <summary>
/// PaymentAndCounterpartyDetail endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class PaymentAndCounterpartyDetailEndpoints
{
    public static void MapPaymentAndCounterpartyDetailEndpoints(this RouteGroupBuilder accounting)
    {

        accounting.MapGet(
            "/receive-payments/{documentId:guid}",
            async (Guid documentId, [AsParameters] ReceivePaymentLookupQuery query, IReceivePaymentDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Receive payment document was not found in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.DocumentDate,
                    CustomerId = document.PartyId,
                    document.BankAccountId,
                    document.ReceivableAccountId,
                    document.RealizedFxGainAccountId,
                    document.RealizedFxLossAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.TotalAmount,
                    document.Memo,
                    Lines = document.PaymentLines.Select(line => new
                    {
                        line.LineNumber,
                        line.TargetArOpenItemId,
                        line.Description,
                        line.AppliedAmount,
                        line.AppliedAmountBase,
                        line.CarryingAmountBase
                    })
                });
            });

        accounting.MapPost(
            "/receive-payments/prepare",
            async (PrepareReceivePaymentDraftHttpRequest request, PrepareReceivePaymentDraftCommandHandler handler, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PrepareReceivePaymentDraftCommand(
                            request.CompanyId,
                            request.UserId,
                            request.CustomerId,
                            request.BankAccountId,
                            request.PaymentDate,
                            request.AcceptedFxSnapshotId,
                            request.Memo,
                            request.Lines.Select(line => new SettlementDraftLine(line.TargetOpenItemId, line.AppliedAmountTx)).ToArray(),
                            request.ExtraDepositAmount),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        // Header surface for the Customer detail page. Read-only aggregate of
        // AR open items: open balance (sum of unsettled amounts in base
        // currency), count of overdue invoices, plus a placeholder for unbilled
        // work (zero today; wired when the Task module ships). The page reads
        // this once per visit and on every "Refresh" click.
        accounting.MapGet(
            "/customers/{customerId:guid}/financial-summary",
            async (
                Guid customerId,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerOverviewQueries queries,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var summary = await queries.GetFinancialSummaryAsync(session.ActiveCompanyId, customerId, cancellationToken);
                return Results.Ok(summary);
            });

        // Unified transaction timeline for the Customer detail page. Returns
        // invoices + sales orders + quotes for one customer, ordered by date
        // desc, with a derived status label (paid / overdue / issued / draft
        // for invoices; raw status for quote / sales order). Filters: type,
        // status (free text contains-match against the derived label), and
        // date range. The Total row is computed client-side off the returned
        // rows so we don't run a second SUM round-trip.
        accounting.MapGet(
            "/customers/{customerId:guid}/transactions",
            async (
                Guid customerId,
                string? type,
                string? status,
                DateOnly? from,
                DateOnly? to,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerOverviewQueries queries,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await queries.ListTransactionsAsync(
                    session.ActiveCompanyId,
                    customerId,
                    new CustomerTransactionFilter(type, status, from, to),
                    cancellationToken);
                return Results.Ok(rows);
            });

        // =====================================================================
        // Customer shipping address book CRUD. Backs the Profile tab's
        // "Shipping addresses" section. The historical-address picker on quote
        // / SO creation continues to read from past documents — these endpoints
        // are the persisted book operators add to deliberately. A follow-up
        // batch will UNION the two sources in the picker.
        // =====================================================================
        accounting.MapGet(
            "/customers/{customerId:guid}/shipping-address-book",
            async (
                Guid customerId,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListAsync(session.ActiveCompanyId, customerId, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapPost(
            "/customers/{customerId:guid}/shipping-address-book",
            async (
                Guid customerId,
                CustomerShippingAddressBookHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(request.AddressLine) &&
                    string.IsNullOrWhiteSpace(request.City) &&
                    string.IsNullOrWhiteSpace(request.PostalCode))
                {
                    return Results.BadRequest(new { message = "Provide at least an address line, city, or postal code." });
                }

                var inserted = await store.InsertAsync(
                    session.ActiveCompanyId,
                    customerId,
                    new CustomerShippingAddressBookUpsertRequest(
                        Label: request.Label,
                        AddressLine: request.AddressLine,
                        City: request.City,
                        ProvinceState: request.ProvinceState,
                        PostalCode: request.PostalCode,
                        Country: request.Country,
                        IsDefault: request.IsDefault),
                    cancellationToken);
                return Results.Ok(inserted);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerEdit);

        accounting.MapPut(
            "/customers/{customerId:guid}/shipping-address-book/{addressId:guid}",
            async (
                Guid customerId,
                Guid addressId,
                CustomerShippingAddressBookHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var updated = await store.UpdateAsync(
                    session.ActiveCompanyId,
                    customerId,
                    addressId,
                    new CustomerShippingAddressBookUpsertRequest(
                        Label: request.Label,
                        AddressLine: request.AddressLine,
                        City: request.City,
                        ProvinceState: request.ProvinceState,
                        PostalCode: request.PostalCode,
                        Country: request.Country,
                        IsDefault: request.IsDefault),
                    cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerEdit);

        accounting.MapDelete(
            "/customers/{customerId:guid}/shipping-address-book/{addressId:guid}",
            async (
                Guid customerId,
                Guid addressId,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var removed = await store.DeleteAsync(session.ActiveCompanyId, customerId, addressId, cancellationToken);
                return removed ? Results.NoContent() : Results.NotFound();
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerEdit);

        accounting.MapPost(
            "/customers/{customerId:guid}/shipping-address-book/{addressId:guid}/set-default",
            async (
                Guid customerId,
                Guid addressId,
                BusinessSessionContextAccessor sessionAccessor,
                ICustomerShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var updated = await store.SetDefaultAsync(session.ActiveCompanyId, customerId, addressId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArCustomerEdit);

        accounting.MapGet(
            "/customers/{customerId:guid}/open-receivables",
            async (Guid customerId, [AsParameters] OpenReceivablesLookupQuery query, IReceivePaymentDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var candidates = await repository.ListOpenReceivableCandidatesAsync(
                    query.CompanyId,
                    customerId,
                    cancellationToken);

                return Results.Ok(candidates.Select(candidate => new
                {
                    candidate.OpenItemId,
                    candidate.SourceType,
                    candidate.SourceDocumentId,
                    candidate.DisplayNumber,
                    candidate.DocumentDate,
                    candidate.DueDate,
                    candidate.DocumentCurrencyCode,
                    candidate.BaseCurrencyCode,
                    candidate.OriginalAmountTx,
                    candidate.OpenAmountTx,
                    candidate.OpenAmountBase,
                    candidate.BalanceSide,
                    candidate.Status
                }));
            });

        accounting.MapPost(
            "/receive-payments/{documentId:guid}/post",
            async (Guid documentId, PostReceivePaymentHttpRequest request, PostReceivePaymentCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostReceivePaymentCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ArReceiptApply);

        accounting.MapGet(
            "/credit-applications/{documentId:guid}",
            async (Guid documentId, [AsParameters] CreditApplicationLookupQuery query, ICreditApplicationDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Credit application document was not found in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.DocumentDate,
                    CustomerId = document.PartyId,
                    ReceivableAccountId = document.ReceivableAccountId,
                    document.RealizedFxGainAccountId,
                    document.RealizedFxLossAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.TotalAmount,
                    document.Memo,
                    Lines = document.ApplicationLines.Select(line => new
                    {
                        line.LineNumber,
                        line.SourceCreditArOpenItemId,
                        line.TargetInvoiceArOpenItemId,
                        line.Description,
                        line.AppliedAmount,
                        line.SourceCarryingAmountBase,
                        line.TargetCarryingAmountBase,
                        line.RealizedFxAmountBase
                    })
                });
            });

        accounting.MapPost(
            "/credit-applications/{documentId:guid}/post",
            async (Guid documentId, PostCreditApplicationHttpRequest request, PostCreditApplicationCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostCreditApplicationCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });

        accounting.MapPost(
            "/pay-bills/prepare",
            async (PreparePayBillDraftHttpRequest request, PreparePayBillDraftCommandHandler handler, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PreparePayBillDraftCommand(
                            request.CompanyId,
                            request.UserId,
                            request.VendorId,
                            request.BankAccountId,
                            request.PaymentDate,
                            request.AcceptedFxSnapshotId,
                            request.Memo,
                            request.Lines.Select(line => new SettlementDraftLine(line.TargetOpenItemId, line.AppliedAmountTx)).ToArray()),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new
                    {
                        message = ex.Message
                    });
                }
            });

        // =====================================================================
        // Vendor detail page surfaces — AP-side mirror of the customer ones.
        // Financial summary returns AP open balance + overdue bill count + open
        // PO count. Transactions UNIONs bills + ap_purchase_orders +
        // vendor_credits ordered by date desc with type/status/from/to filters.
        // =====================================================================
        accounting.MapGet(
            "/vendors/{vendorId:guid}/financial-summary",
            async (
                Guid vendorId,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorOverviewQueries queries,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var summary = await queries.GetFinancialSummaryAsync(session.ActiveCompanyId, vendorId, cancellationToken);
                return Results.Ok(summary);
            });

        accounting.MapGet(
            "/vendors/{vendorId:guid}/transactions",
            async (
                Guid vendorId,
                string? type,
                string? status,
                DateOnly? from,
                DateOnly? to,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorOverviewQueries queries,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await queries.ListTransactionsAsync(
                    session.ActiveCompanyId,
                    vendorId,
                    new VendorTransactionFilter(type, status, from, to),
                    cancellationToken);
                return Results.Ok(rows);
            });

        // Vendor shipping address book CRUD (mirror of the customer set).
        accounting.MapGet(
            "/vendors/{vendorId:guid}/shipping-address-book",
            async (
                Guid vendorId,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var rows = await store.ListAsync(session.ActiveCompanyId, vendorId, cancellationToken);
                return Results.Ok(rows);
            });

        accounting.MapPost(
            "/vendors/{vendorId:guid}/shipping-address-book",
            async (
                Guid vendorId,
                VendorShippingAddressBookHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(request.AddressLine) &&
                    string.IsNullOrWhiteSpace(request.City) &&
                    string.IsNullOrWhiteSpace(request.PostalCode))
                {
                    return Results.BadRequest(new { message = "Provide at least an address line, city, or postal code." });
                }

                var inserted = await store.InsertAsync(
                    session.ActiveCompanyId,
                    vendorId,
                    new VendorShippingAddressBookUpsertRequest(
                        Label: request.Label,
                        AddressLine: request.AddressLine,
                        City: request.City,
                        ProvinceState: request.ProvinceState,
                        PostalCode: request.PostalCode,
                        Country: request.Country,
                        IsDefault: request.IsDefault),
                    cancellationToken);
                return Results.Ok(inserted);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorEdit);

        accounting.MapPut(
            "/vendors/{vendorId:guid}/shipping-address-book/{addressId:guid}",
            async (
                Guid vendorId,
                Guid addressId,
                VendorShippingAddressBookHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var updated = await store.UpdateAsync(
                    session.ActiveCompanyId,
                    vendorId,
                    addressId,
                    new VendorShippingAddressBookUpsertRequest(
                        Label: request.Label,
                        AddressLine: request.AddressLine,
                        City: request.City,
                        ProvinceState: request.ProvinceState,
                        PostalCode: request.PostalCode,
                        Country: request.Country,
                        IsDefault: request.IsDefault),
                    cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorEdit);

        accounting.MapDelete(
            "/vendors/{vendorId:guid}/shipping-address-book/{addressId:guid}",
            async (
                Guid vendorId,
                Guid addressId,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var removed = await store.DeleteAsync(session.ActiveCompanyId, vendorId, addressId, cancellationToken);
                return removed ? Results.NoContent() : Results.NotFound();
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorEdit);

        accounting.MapPost(
            "/vendors/{vendorId:guid}/shipping-address-book/{addressId:guid}/set-default",
            async (
                Guid vendorId,
                Guid addressId,
                BusinessSessionContextAccessor sessionAccessor,
                IVendorShippingAddressBookStore store,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value)) return Results.Unauthorized();

                var updated = await store.SetDefaultAsync(session.ActiveCompanyId, vendorId, addressId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApVendorEdit);

        accounting.MapGet(
            "/vendors/{vendorId:guid}/open-payables",
            async (Guid vendorId, [AsParameters] OpenPayablesLookupQuery query, IPayBillDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var candidates = await repository.ListOpenPayableCandidatesAsync(
                    query.CompanyId,
                    vendorId,
                    cancellationToken);

                return Results.Ok(candidates.Select(candidate => new
                {
                    candidate.OpenItemId,
                    candidate.SourceType,
                    candidate.SourceDocumentId,
                    candidate.DisplayNumber,
                    candidate.DocumentDate,
                    candidate.DueDate,
                    candidate.DocumentCurrencyCode,
                    candidate.BaseCurrencyCode,
                    candidate.OriginalAmountTx,
                    candidate.OpenAmountTx,
                    candidate.OpenAmountBase,
                    candidate.BalanceSide,
                    candidate.Status
                }));
            });

        accounting.MapGet(
            "/pay-bills/{documentId:guid}",
            async (Guid documentId, [AsParameters] PayBillLookupQuery query, IPayBillDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Pay bill document was not found in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.DocumentDate,
                    VendorId = document.PartyId,
                    document.BankAccountId,
                    document.PayableAccountId,
                    document.RealizedFxGainAccountId,
                    document.RealizedFxLossAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.TotalAmount,
                    document.Memo,
                    Lines = document.PaymentLines.Select(line => new
                    {
                        line.LineNumber,
                        line.TargetApOpenItemId,
                        line.Description,
                        line.AppliedAmount,
                        line.AppliedAmountBase,
                        line.CarryingAmountBase
                    })
                });
            });

        accounting.MapPost(
            "/pay-bills/{documentId:guid}/post",
            async (Guid documentId, PostPayBillHttpRequest request, PostPayBillCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostPayBillCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            request.AcceptedFxSnapshotId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.ApPaymentApply);

        accounting.MapGet(
            "/vendor-credit-applications/{documentId:guid}",
            async (Guid documentId, [AsParameters] VendorCreditApplicationLookupQuery query, IVendorCreditApplicationDocumentRepository repository, CancellationToken cancellationToken) =>
            {
                var document = await repository.GetForPostingAsync(
                    query.CompanyId,
                    documentId,
                    cancellationToken);

                if (document is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Vendor credit application document was not found in the active company context."
                    });
                }

                return Results.Ok(new
                {
                    document.Id,
                    CompanyId = document.CompanyId,
                    EntityNumber = document.EntityNumber.Value,
                    DisplayNumber = document.DisplayNumber.Value,
                    document.Status,
                    document.DocumentDate,
                    VendorId = document.PartyId,
                    PayableAccountId = document.PayableAccountId,
                    document.RealizedFxGainAccountId,
                    document.RealizedFxLossAccountId,
                    TransactionCurrencyCode = document.TransactionCurrencyCode.Value,
                    BaseCurrencyCode = document.BaseCurrencyCode.Value,
                    document.TotalAmount,
                    document.Memo,
                    Lines = document.ApplicationLines.Select(line => new
                    {
                        line.LineNumber,
                        line.SourceVendorCreditApOpenItemId,
                        line.TargetBillApOpenItemId,
                        line.Description,
                        line.AppliedAmount,
                        line.SourceCarryingAmountBase,
                        line.TargetCarryingAmountBase,
                        line.RealizedFxAmountBase
                    })
                });
            });

        accounting.MapPost(
            "/vendor-credit-applications/{documentId:guid}/post",
            async (Guid documentId, PostVendorCreditApplicationHttpRequest request, PostVendorCreditApplicationCommandHandler handler, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await handler.HandleAsync(
                        new PostVendorCreditApplicationCommand(
                            request.CompanyId,
                            documentId,
                            request.UserId,
                            ResolveIdempotencyKey(httpContext, request.IdempotencyKey)),
                        cancellationToken);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return AccountingOperationBadRequest(ex);
                }
            });
    }
}
