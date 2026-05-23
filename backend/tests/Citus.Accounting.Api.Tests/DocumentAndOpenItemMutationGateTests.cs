using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Platform.Core.Runtime;
using Citus.Ui.Shared.Business;
using Microsoft.AspNetCore.Http;
using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;

namespace Citus.Accounting.Api.Tests;

public sealed class DocumentAndOpenItemMutationGateTests
{
    private static readonly UserId UserId = UserId.FromOrdinal(1);
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid DocumentId = Guid.Parse("b5e93d57-d503-4585-b444-40a0018fe100");
    private static readonly Guid OpenItemId = Guid.Parse("fd5ff873-4fd9-4b10-b35d-8bdb378e6505");
    private static readonly Guid RequestId = Guid.Parse("416693b4-c2e5-488d-ba79-6878e5ed7d66");
    private static readonly Guid CustomerId = Guid.Parse("d6400ad5-8e86-4dde-a013-8be82013d881");
    private static readonly Guid VendorId = Guid.Parse("648622e5-f9b4-4a2e-b22c-81cf3fb94377");
    private static readonly Guid BankAccountId = Guid.Parse("b00d9e9e-f004-4550-b9f9-f3159390615e");
    private static readonly Guid RevenueAccountId = Guid.Parse("f9db6a98-4f66-4524-8570-8e95bb17c35f");
    private static readonly Guid AdjustmentAccountId = Guid.Parse("a8f37f3a-b998-474c-9fb7-b81b78d0b006");

    [Fact]
    public async Task InvoiceDraftSaveEndpoint_BlocksWrite_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var request = new SaveInvoiceDraftHttpRequest(
            CompanyId,
            CustomerId,
            new DateOnly(2026, 04, 15),
            new DateOnly(2026, 05, 15),
            "USD",
            "USD",
            null,
            null,
            null,
            null,
            "draft memo",
            [
                new SaveInvoiceDraftLineHttpRequest(
                    1,
                    RevenueAccountId,
                    "Design work",
                    1m,
                    100m,
                    null,
                    0m)
            ]);

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [request],
            maintenanceState: null,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("read-only access only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvoiceDraftLookupEndpoint_AllowsRead_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var query = new InvoiceLookupQuery(CompanyId);

        var result = await guard.EvaluateAsync(
            HttpMethods.Get,
            CreateHeaders(UserId, CompanyId),
            [query],
            maintenanceState: null,
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.NotNull(result.Resolution);
        Assert.True(result.Resolution!.ActiveCompany.IsReadOnly);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public async Task SourceDocumentReverseEndpoint_BlocksWrite_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard(CreateActiveContext());
        var query = new DocumentReviewLookupQuery(CompanyId);

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            ["invoice", DocumentId, query],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    [Fact]
    public async Task SourceDocumentReverseRequestLookupEndpoint_AllowsRead_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var query = new DocumentReviewLookupQuery(CompanyId);

        var result = await guard.EvaluateAsync(
            HttpMethods.Get,
            CreateHeaders(UserId, CompanyId),
            ["invoice", DocumentId, query],
            maintenanceState: null,
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.NotNull(result.Resolution);
        Assert.True(result.Resolution!.ActiveCompany.IsReadOnly);
    }

    [Fact]
    public async Task ArOpenItemAdjustmentRequestEndpoint_BlocksWrite_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var request = new RequestOpenItemAdjustmentHttpRequest(
            CompanyId,
            "write_off",
            new DateOnly(2026, 04, 15),
            25m,
            "Small residual balance");

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [OpenItemId, request],
            maintenanceState: null,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("read-only access only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApOpenItemAdjustmentExecuteEndpoint_BlocksWrite_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard(CreateActiveContext());
        var request = new ExecuteOpenItemAdjustmentRequestHttpRequest(
            CompanyId,
            AdjustmentAccountId,
            new DateOnly(2026, 04, 15),
            "execute-adjustment");

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [OpenItemId, RequestId, request],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    [Fact]
    public async Task ReceivePaymentPrepareEndpoint_BlocksWrite_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var request = new PrepareReceivePaymentDraftHttpRequest(
            CompanyId,
            CustomerId,
            BankAccountId,
            new DateOnly(2026, 04, 15),
            null,
            "receipt memo",
            [
                new PrepareSettlementDraftLineHttpRequest(OpenItemId, 50m)
            ]);

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [request],
            maintenanceState: null,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("read-only access only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayBillPostEndpoint_BlocksWrite_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard(CreateActiveContext());
        var request = new PostPayBillHttpRequest(
            CompanyId,
            null,
            "post-pay-bill");

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [DocumentId, request],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    [Fact]
    public async Task SourceDocumentReverseRequestSubmitEndpoint_BlocksWrite_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var query = new DocumentReviewLookupQuery(CompanyId);

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            ["invoice", DocumentId, RequestId, query],
            maintenanceState: null,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("read-only access only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceDocumentReverseRequestExecuteEndpoint_BlocksWrite_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard(CreateActiveContext());
        var query = new DocumentLifecycleRequestReadinessQuery(
            CompanyId,
            new DateOnly(2026, 04, 15));

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            ["invoice", DocumentId, RequestId, query],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    [Fact]
    public async Task AdjustmentAccountMappingSaveEndpoint_BlocksWrite_WhenCompanyIsReadOnly()
    {
        var guard = CreateGuard(CreateInactiveContext());
        var request = new SaveOpenItemAdjustmentAccountMappingHttpRequest(
            CompanyId,
            null,
            "ar_open_item",
            "write_off",
            AdjustmentAccountId);

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [request],
            maintenanceState: null,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("read-only access only", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdjustmentAccountMappingDeactivateEndpoint_BlocksWrite_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard(CreateActiveContext());
        var request = new DeactivateOpenItemAdjustmentAccountMappingHttpRequest(
            CompanyId);

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [RequestId, request],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    private static BusinessRouteGuard CreateGuard(CompanyAccessSessionContext context) =>
        new(
            new BusinessSessionRequestReader(),
            new BusinessRequestContractGuard(),
            new BusinessSessionDirectory(
                Microsoft.Extensions.Options.Options.Create(CreateFixtureOptions()),
                new StubCompanySessionContextWorkflow(context)),
            new StubPlatformBusinessSessionRepository());

    // Test-local fixture. Production directory no longer carries built-ins.
    private static BusinessSessionOptions CreateFixtureOptions() => new()
    {
        Companies =
        [
            new BusinessSessionCompanyOptions
            {
                Id = CompanyId,
                CompanyCode = "NORTHWIND",
                CompanyName = "Northwind Studio Ltd.",
                BaseCurrencyCode = "USD",
                MultiCurrencyEnabled = true
            }
        ],
        Users =
        [
            new BusinessSessionUserOptions
            {
                Id = UserId,
                DisplayName = "Alice Rowan",
                Email = "alice.rowan@northwind.example",
                Username = "alice.rowan",
                Roles = ["owner", "reports"],
                CompanyIds = [CompanyId]
            }
        ]
    };

    private static HeaderDictionary CreateHeaders(UserId userId, CompanyId companyId) =>
        new()
        {
            [BusinessAuthHeaderNames.SessionToken] = "session-test",
            [BusinessSessionHeaders.UserId] = userId.ToString(),
            [BusinessSessionHeaders.ActiveCompanyId] = companyId.ToString()
        };

    private static CompanyAccessSessionContext CreateActiveContext() =>
        CreateContext("active");

    private static CompanyAccessSessionContext CreateInactiveContext() =>
        CreateContext("inactive");

    private static CompanyAccessSessionContext CreateContext(string status) =>
        new()
        {
            User = new CompanyAccessUserSummary
            {
                Id = UserId,
                DisplayName = "Persisted Owner",
                Email = "persisted.owner@example.test",
                Username = "persisted.owner",
                Roles = ["owner"]
            },
            ActiveCompany = CreateCompanySummary(status),
            AvailableCompanies = [CreateCompanySummary(status)]
        };

    private static CompanyAccessCompanySummary CreateCompanySummary(string status) =>
        new()
        {
            Id = CompanyId,
            CompanyCode = "NORTHWIND",
            CompanyName = "Northwind Studio Ltd.",
            BaseCurrencyCode = "USD",
            MultiCurrencyEnabled = true,
            Status = status,
            IsReadOnly = !string.Equals(status, "active", StringComparison.Ordinal)
        };

    private sealed class StubCompanySessionContextWorkflow(CompanyAccessSessionContext? context) : ICompanySessionContextWorkflow
    {
        public Task<CompanyAccessSessionContext?> GetAsync(
            UserId userId,
            CompanyId? preferredActiveCompanyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(context);
    }

    private sealed class StubPlatformBusinessSessionRepository : IPlatformBusinessSessionRepository
    {
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformBusinessSessionResult> AuthenticateAsync(
            string login,
            string password,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlatformBusinessSessionResult> ValidateSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PlatformBusinessSessionResult
            {
                Succeeded = string.Equals(sessionToken, "session-test", StringComparison.Ordinal),
                UserId = UserId,
                ActiveCompanyId = CompanyId
            });

        public Task<PlatformBusinessSessionResult> SwitchActiveCompanyAsync(
            string sessionToken,
            CompanyId activeCompanyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlatformBusinessSessionResult> CompleteSecondFactorAsync(
            Guid challengeId,
            string verificationCode,
            TimeSpan sessionLifetime,
            string? remoteIp,
            string? userAgent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RevokeSessionAsync(
            string sessionToken,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
