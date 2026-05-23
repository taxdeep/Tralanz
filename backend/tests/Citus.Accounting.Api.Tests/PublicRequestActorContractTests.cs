using System.Reflection;
using System.Text.Json;
using Citus.Accounting.Api;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class PublicRequestActorContractTests
{
    private static readonly UserId SessionUserId = UserId.FromOrdinal(1);
    private static readonly CompanyId SessionCompanyId = CompanyId.FromOrdinal(1);
    private static readonly CompanyId OtherCompanyId = CompanyId.FromOrdinal(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> SessionActorOnlyRequestTypes => new()
    {
        "UnitySearchHttpQuery",
        "UnitySearchRecentHttpQuery",
        "UnitySearchClickHttpRequest",
        "PrepareFxRevaluationBatchHttpRequest",
        "PrepareFxRevaluationUnwindBatchHttpRequest",
        "PrepareCompanyBookGovernedChangeRequestHttpRequest",
        "TransitionCompanyBookGovernedChangeRequestHttpRequest",
        "CreateCompanyBookGovernanceSignalHttpRequest",
        "RegisterCompanyBookClosedPeriodHttpRequest",
        "RegisterCompanyBookIssuedStatementHttpRequest",
        "RegisterCompanyBookFiledTaxHttpRequest",
        "PostManualJournalHttpRequest",
        "RequestOpenItemAdjustmentHttpRequest",
        "TransitionOpenItemAdjustmentRequestHttpRequest",
        "GovernOpenItemAdjustmentApprovalHttpRequest",
        "ExecuteOpenItemAdjustmentRequestHttpRequest",
        "SaveOpenItemAdjustmentAccountMappingHttpRequest",
        "DeactivateOpenItemAdjustmentAccountMappingHttpRequest",
        "SaveReceiptDraftHttpRequest",
        "PostReceiptDraftHttpRequest",
        "PostReceiptGrIrBridgeHttpRequest",
        "ExecuteReceiptGrIrSettlementHttpRequest",
        "PostReceiptGrIrSettlementJournalHttpRequest",
        "SaveReceiptGrIrClearingAccountPolicyHttpRequest",
        "SavePurchaseOrderDraftHttpRequest",
        "RequestPurchaseOrderApprovalHttpRequest",
        "SubmitPurchaseOrderApprovalRequestHttpRequest",
        "RejectPurchaseOrderApprovalRequestHttpRequest",
        "ReversePurchaseOrderApprovalHttpRequest",
        "ApprovePurchaseOrderHttpRequest",
        "IssuePurchaseOrderHttpRequest",
        "ReopenPurchaseOrderForAmendmentHttpRequest",
        "ClosePurchaseOrderHttpRequest",
        "CancelPurchaseOrderHttpRequest",
        "RefreshPurchaseOrderQuantityDiscrepanciesHttpRequest",
        "ReviewPurchaseOrderQuantityDiscrepancyHttpRequest",
        "PostVendorCreditHttpRequest",
        "SaveVendorCreditDraftHttpRequest",
        "PostCreditApplicationHttpRequest",
        "PostVendorCreditApplicationHttpRequest",
        "SalesReceiptSaveAndPostHttpRequest",
        "RefundReceiptSaveAndPostHttpRequest",
        "CreditMemoSaveAndPostHttpRequest",
        "VendorCreditSaveAndPostHttpRequest",
        "BankTransferSaveAndPostHttpRequest",
        "BankDepositSaveAndPostHttpRequest",
        "TaxReturnSaveAndPostHttpRequest",
        "PostInvoiceHttpRequest",
        "SaveInvoiceDraftHttpRequest",
        "PostCreditNoteHttpRequest",
        "SaveCreditNoteDraftHttpRequest",
        "PostBillHttpRequest",
        "SaveBillDraftHttpRequest",
        "SubmitBillDraftHttpRequest",
        "PrepareReceivePaymentDraftHttpRequest",
        "PostReceivePaymentHttpRequest",
        "PostPayBillHttpRequest",
        "PreparePayBillDraftHttpRequest",
        "PostFxRevaluationBatchHttpRequest"
    };

    [Theory]
    [MemberData(nameof(SessionActorOnlyRequestTypes))]
    public void PublicRequestContracts_DoNotExposeCallerSuppliedUserId(string typeName)
    {
        var type = ResolveApiContract(typeName);

        Assert.Null(type.GetProperty("UserId", BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void ApiWireRequestAndQueryContracts_DoNotExposeCallerSuppliedUserId()
    {
        var offenders = typeof(UnitySearchPermissionFilter).Assembly.GetTypes()
            .Where(static type =>
                string.Equals(type.Namespace, "Citus.Accounting.Api", StringComparison.Ordinal) &&
                IsWireRequestOrQueryContract(type) &&
                type.GetProperty("UserId", BindingFlags.Instance | BindingFlags.Public) is not null)
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void InventoryItemWireRequest_MapsActorFromSessionEvenWhenLegacyJsonContainsUserId()
    {
        var spoofedUserId = UserId.FromOrdinal(99);
        var json = $$"""
            {
              "userId": "{{spoofedUserId}}",
              "itemCode": "SKU-001",
              "name": "Session-owned item",
              "itemKind": "stock",
              "defaultSalesPrice": 25.50,
              "defaultPurchasePrice": 10.25
            }
            """;

        var wireRequest = JsonSerializer.Deserialize<InventoryItemUpsertHttpRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Could not deserialize inventory item request.");

        var mapped = MapInventoryItemRequest(SessionCompanyId, SessionUserId, itemId: null, wireRequest);

        Assert.Equal(SessionCompanyId, mapped.CompanyId);
        Assert.Equal(SessionUserId, mapped.UserId);
        Assert.NotEqual(spoofedUserId, mapped.UserId);
    }

    [Fact]
    public void InventoryActivationWireRequest_MapsActorFromSessionEvenWhenLegacyJsonContainsUserId()
    {
        var spoofedUserId = UserId.FromOrdinal(99);
        var warehouseId = Guid.NewGuid();
        var json = $$"""
            {
              "userId": "{{spoofedUserId}}",
              "costingMethod": "fifo",
              "warehouseName": "  Operations Warehouse  "
            }
            """;

        var wireRequest = JsonSerializer.Deserialize<InventoryActivationHttpRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Could not deserialize inventory activation request.");

        var policy = MapInventoryActivationPolicyRequest(SessionCompanyId, SessionUserId, wireRequest);
        var warehouse = MapInventoryActivationWarehouseRequest(SessionCompanyId, SessionUserId, warehouseId, wireRequest);

        Assert.Equal(SessionCompanyId, policy.CompanyId);
        Assert.Equal(SessionUserId, policy.UserId);
        Assert.NotEqual(spoofedUserId, policy.UserId);
        Assert.Equal(InventoryCostingMethod.Fifo, policy.DefaultCostingMethod);

        Assert.Equal(SessionCompanyId, warehouse.CompanyId);
        Assert.Equal(SessionUserId, warehouse.UserId);
        Assert.NotEqual(spoofedUserId, warehouse.UserId);
        Assert.Equal(warehouseId, warehouse.WarehouseId);
        Assert.Equal("MAIN", warehouse.WarehouseCode);
        Assert.Equal("Operations Warehouse", warehouse.Name);
    }

    [Fact]
    public void WarehouseRenameWireRequest_MapsActorFromSessionEvenWhenLegacyJsonContainsUserId()
    {
        var spoofedUserId = UserId.FromOrdinal(99);
        var warehouseId = Guid.NewGuid();
        var json = $$"""
            {
              "userId": "{{spoofedUserId}}",
              "warehouseCode": " east ",
              "name": "  East Warehouse  ",
              "description": "  Secondary stock location  "
            }
            """;

        var wireRequest = JsonSerializer.Deserialize<WarehouseRenameHttpRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Could not deserialize warehouse rename request.");

        var mapped = MapWarehouseRenameRequest(SessionCompanyId, SessionUserId, warehouseId, wireRequest);

        Assert.Equal(SessionCompanyId, mapped.CompanyId);
        Assert.Equal(SessionUserId, mapped.UserId);
        Assert.NotEqual(spoofedUserId, mapped.UserId);
        Assert.Equal(warehouseId, mapped.WarehouseId);
        Assert.Equal("EAST", mapped.WarehouseCode);
        Assert.Equal("East Warehouse", mapped.Name);
        Assert.Equal("Secondary stock location", mapped.Description);
    }

    [Fact]
    public void UnitysearchUsageWireRequest_MapsActorFromSessionEvenWhenLegacyJsonContainsUserId()
    {
        var spoofedUserId = UserId.FromOrdinal(99);
        var selectedEntityId = Guid.NewGuid();
        var json = $$"""
            {
              "companyId": "{{SessionCompanyId}}",
              "userId": "{{spoofedUserId}}",
              "sessionId": "search-session",
              "context": " global ",
              "entityType": " invoice ",
              "query": "  INV-100 ",
              "eventType": " select ",
              "selectedEntityId": "{{selectedEntityId}}",
              "rankPosition": 2,
              "resultCount": 9
            }
            """;

        var wireRequest = JsonSerializer.Deserialize<UnitysearchUsageHttpRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Could not deserialize unitysearch usage request.");

        var mapped = MapUnitysearchUsageRequest(SessionCompanyId, SessionUserId, wireRequest);

        Assert.Equal(SessionCompanyId, mapped.CompanyId);
        Assert.Equal(SessionUserId, mapped.UserId);
        Assert.NotEqual(spoofedUserId, mapped.UserId);
        Assert.Equal("global", mapped.Context);
        Assert.Equal("invoice", mapped.EntityType);
        Assert.Equal("select", mapped.EventType);
        Assert.Equal("inv-100", mapped.NormalizedQuery);
        Assert.Equal(selectedEntityId, mapped.SelectedEntityId);
    }

    [Fact]
    public void ReportUsageWireRequest_MapsActorFromSessionEvenWhenLegacyJsonContainsUserId()
    {
        var spoofedUserId = UserId.FromOrdinal(99);
        var json = $$"""
            {
              "userId": "{{spoofedUserId}}",
              "reportKey": " trial-balance ",
              "eventType": " export ",
              "dateRangeKey": "this-month",
              "sourceRoute": "/reports/trial-balance"
            }
            """;

        var wireRequest = JsonSerializer.Deserialize<ReportUsageHttpRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Could not deserialize report usage request.");

        var mapped = MapReportUsageRequest(SessionCompanyId, SessionUserId, wireRequest);

        Assert.Equal(SessionCompanyId, mapped.CompanyId);
        Assert.Equal(SessionUserId, mapped.UserId);
        Assert.NotEqual(spoofedUserId, mapped.UserId);
        Assert.Equal("trial-balance", mapped.ReportKey);
        Assert.Equal("export", mapped.EventType);
        Assert.Equal("this-month", mapped.DateRangeKey);
    }

    [Theory]
    [InlineData("UnitySearchHttpQuery")]
    [InlineData("UnitySearchRecentHttpQuery")]
    [InlineData("SalesReceiptSaveAndPostHttpRequest")]
    [InlineData("TaxReturnSaveAndPostHttpRequest")]
    public void PublicRequestContracts_RemainCompanyScopedByGuard(string typeName)
    {
        var guard = new BusinessRequestContractGuard();
        var request = CreateContract(typeName, OtherCompanyId);

        var result = guard.Validate([request], CreateSession());

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("does not match the active company context", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UnitySearchHttpQuery")]
    [InlineData("UnitySearchRecentHttpQuery")]
    [InlineData("SalesReceiptSaveAndPostHttpRequest")]
    [InlineData("TaxReturnSaveAndPostHttpRequest")]
    public void PublicRequestContracts_AllowMatchingCompanyWithoutCallerUserId(string typeName)
    {
        var guard = new BusinessRequestContractGuard();
        var request = CreateContract(typeName, SessionCompanyId);

        var result = guard.Validate([request], CreateSession());

        Assert.True(result.Allowed);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Theory]
    [MemberData(nameof(SessionActorOnlyRequestTypes))]
    public void PublicRequestContracts_IgnoreLegacyJsonUserId_WhenCompanyMatches(string typeName)
    {
        var guard = new BusinessRequestContractGuard();
        var type = ResolveApiContract(typeName);
        var json = $$"""
            {
              "companyId": "{{SessionCompanyId}}",
              "userId": "{{UserId.FromOrdinal(2)}}"
            }
            """;

        var request = JsonSerializer.Deserialize(json, type, JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize {typeName}.");

        var result = guard.Validate([request], CreateSession());

        Assert.True(result.Allowed);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    private static BusinessSessionContext CreateSession() =>
        new()
        {
            UserId = SessionUserId,
            ActiveCompanyId = SessionCompanyId
        };

    private static object CreateContract(string typeName, CompanyId companyId)
    {
        var type = ResolveApiContract(typeName);
        var instance = Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException($"Could not create {typeName}.");

        var companyProperty = type.GetProperty("CompanyId", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{typeName} does not expose CompanyId.");

        companyProperty.SetValue(instance, companyId);
        return instance;
    }

    private static Type ResolveApiContract(string typeName)
    {
        var assembly = typeof(UnitySearchPermissionFilter).Assembly;
        return assembly.GetType(typeName)
            ?? assembly.GetType($"Citus.Accounting.Api.{typeName}")
            ?? throw new InvalidOperationException($"Could not resolve API contract type '{typeName}'.");
    }

    private static InventoryItemUpsertRequest MapInventoryItemRequest(
        CompanyId companyId,
        UserId userId,
        Guid? itemId,
        InventoryItemUpsertHttpRequest request)
    {
        var mapperType = typeof(InventoryItemUpsertHttpRequest).Assembly.GetType("Citus.Accounting.Api.InventoryItemRequestMapper")
            ?? throw new InvalidOperationException("Could not resolve inventory item request mapper.");
        var mapper = mapperType.GetMethod(
                "BuildItemUpsertRequest",
                BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("Could not resolve inventory item request mapper method.");

        return (InventoryItemUpsertRequest)(mapper.Invoke(null, [companyId, userId, itemId, request])
            ?? throw new InvalidOperationException("Inventory item request mapper returned null."));
    }

    private static InventoryCostingPolicyUpdateRequest MapInventoryActivationPolicyRequest(
        CompanyId companyId,
        UserId userId,
        InventoryActivationHttpRequest request)
    {
        var mapper = ResolveApiStaticMethod(
            "Citus.Accounting.Api.InventoryActivationRequestParser",
            "BuildPolicyUpdateRequest");

        return (InventoryCostingPolicyUpdateRequest)(mapper.Invoke(null, [companyId, userId, request])
            ?? throw new InvalidOperationException("Inventory activation policy mapper returned null."));
    }

    private static InventoryWarehouseUpsertRequest MapInventoryActivationWarehouseRequest(
        CompanyId companyId,
        UserId userId,
        Guid warehouseId,
        InventoryActivationHttpRequest request)
    {
        var mapper = ResolveApiStaticMethod(
            "Citus.Accounting.Api.InventoryActivationRequestParser",
            "BuildDefaultWarehouseRequest");

        return (InventoryWarehouseUpsertRequest)(mapper.Invoke(null, [companyId, userId, warehouseId, request])
            ?? throw new InvalidOperationException("Inventory activation warehouse mapper returned null."));
    }

    private static InventoryWarehouseUpsertRequest MapWarehouseRenameRequest(
        CompanyId companyId,
        UserId userId,
        Guid warehouseId,
        WarehouseRenameHttpRequest request)
    {
        var mapper = ResolveApiStaticMethod(
            "Citus.Accounting.Api.WarehouseRequestMapper",
            "BuildWarehouseUpsertRequest");

        return (InventoryWarehouseUpsertRequest)(mapper.Invoke(null, [companyId, userId, warehouseId, request])
            ?? throw new InvalidOperationException("Warehouse rename mapper returned null."));
    }

    private static UnitysearchEventInput MapUnitysearchUsageRequest(
        CompanyId companyId,
        UserId userId,
        UnitysearchUsageHttpRequest request)
    {
        var mapper = ResolveApiStaticMethod(
            "Citus.Accounting.Api.UnityAiHttpRequestMapper",
            "BuildUnitysearchEventInput");

        return (UnitysearchEventInput)(mapper.Invoke(null, [companyId, userId, request])
            ?? throw new InvalidOperationException("Unitysearch usage mapper returned null."));
    }

    private static ReportUsageEventInput MapReportUsageRequest(
        CompanyId companyId,
        UserId userId,
        ReportUsageHttpRequest request)
    {
        var mapper = ResolveApiStaticMethod(
            "Citus.Accounting.Api.UnityAiHttpRequestMapper",
            "BuildReportUsageEventInput");

        return (ReportUsageEventInput)(mapper.Invoke(null, [companyId, userId, request])
            ?? throw new InvalidOperationException("Report usage mapper returned null."));
    }

    private static MethodInfo ResolveApiStaticMethod(string typeName, string methodName)
    {
        var mapperType = typeof(UnitySearchPermissionFilter).Assembly.GetType(typeName)
            ?? throw new InvalidOperationException($"Could not resolve {typeName}.");
        return mapperType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Could not resolve {typeName}.{methodName}.");
    }

    private static bool IsWireRequestOrQueryContract(Type type) =>
        type.Name.EndsWith("HttpRequest", StringComparison.Ordinal) ||
        type.Name.EndsWith("HttpQuery", StringComparison.Ordinal) ||
        type.Name.EndsWith("LookupQuery", StringComparison.Ordinal) ||
        type.Name.EndsWith("Query", StringComparison.Ordinal) ||
        type.Name.EndsWith("Request", StringComparison.Ordinal);
}
