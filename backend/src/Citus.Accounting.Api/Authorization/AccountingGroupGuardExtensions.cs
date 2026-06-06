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

namespace Citus.Accounting.Api.Authorization;

/// <summary>
/// The /accounting group guard extracted verbatim from Program.cs (P7): the
/// per-request endpoint filter that enforces maintenance mode + the business
/// session (BusinessRouteGuard) and publishes the resolved session on the
/// accessor. Behavior identical; this only relocates the wiring.
/// </summary>
public static class AccountingGroupGuardExtensions
{
    public static RouteGroupBuilder AddBusinessSessionGuard(this RouteGroupBuilder accounting)
    {
        accounting.AddEndpointFilterFactory(
            (factoryContext, next) =>
            {
                return async invocationContext =>
                {
                    var services = invocationContext.HttpContext.RequestServices;
                    var runtimeStateRepository = services.GetRequiredService<IPlatformRuntimeStateRepository>();
                    var routeGuard = services.GetRequiredService<BusinessRouteGuard>();
                    var sessionAccessor = services.GetRequiredService<BusinessSessionContextAccessor>();
                    var maintenanceState = await runtimeStateRepository.GetMaintenanceStateAsync(invocationContext.HttpContext.RequestAborted);
                    var guardResult = await routeGuard.EvaluateAsync(
                        invocationContext.HttpContext.Request.Method,
                        invocationContext.HttpContext.Request.Headers,
                        invocationContext.Arguments as IReadOnlyList<object?> ?? invocationContext.Arguments.ToArray(),
                        maintenanceState,
                        invocationContext.HttpContext.RequestAborted);

                    if (!guardResult.Allowed)
                    {
                        return Results.Json(
                            new
                            {
                                message = guardResult.Message,
                                maintenanceEnabled = maintenanceState?.Enabled ?? false,
                                maintenanceMessage = maintenanceState?.Message,
                                scheduledUntilUtc = maintenanceState?.ScheduledUntilUtc,
                                requiredHeaders = new[]
                                {
                                    Citus.Ui.Shared.Business.BusinessAuthHeaderNames.SessionToken,
                                    BusinessSessionHeaders.UserId,
                                    BusinessSessionHeaders.ActiveCompanyId
                                }
                            },
                            statusCode: guardResult.StatusCode);
                    }

                    if (guardResult.Session is not null)
                    {
                        sessionAccessor.Set(guardResult.Session, guardResult.Resolution);
                    }

                    return await next(invocationContext);
                };
            });
        return accounting;
    }
}
