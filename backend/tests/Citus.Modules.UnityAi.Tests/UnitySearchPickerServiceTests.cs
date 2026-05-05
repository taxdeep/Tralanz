using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Citus.Modules.UnitySearch.Blazor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Citus.Modules.UnityAi.Tests;

/// <summary>
/// Verifies that the picker service emits a correctly-shaped usage event
/// against <c>POST /accounting/unitysearch/usage</c> and that any failure
/// is swallowed (search UX must never break because tracking failed).
/// </summary>
public sealed class UnitySearchPickerServiceTests
{
    [Fact]
    public async Task RecordUsageAsync_PostsExpectedShape()
    {
        var captured = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(captured) { BaseAddress = new Uri("http://api.test/") };
        var service = new UnitySearchPickerService(http, NullLogger<UnitySearchPickerService>.Instance);

        var companyId = CompanyId.FromOrdinal(1);
        var entityId = Guid.NewGuid();
        var anchorId = Guid.NewGuid();

        await service.RecordUsageAsync(new UnitysearchUsageEvent
        {
            CompanyId = companyId,
            Context = "expense.vendor_picker",
            EntityType = "vendor",
            Query = "amazon",
            EventType = "select",
            SelectedEntityId = entityId,
            RankPosition = 2,
            ResultCount = 8,
            SourceRoute = "/expenses/new",
            AnchorContext = "expense.payment_account_picker",
            AnchorEntityType = "account",
            AnchorEntityId = anchorId,
        }, CancellationToken.None);

        Assert.NotNull(captured.LastRequest);
        Assert.Equal(HttpMethod.Post, captured.LastRequest!.Method);
        Assert.Equal("http://api.test/accounting/unitysearch/usage", captured.LastRequest.RequestUri!.ToString());

        // PostAsJsonAsync uses the default camelCase naming policy. The
        // ASP.NET Core API layer accepts both via its case-insensitive
        // model binder, so the wire format is fine; this test pins the
        // exact shape the client emits today.
        var body = JsonDocument.Parse(captured.LastBody!);
        Assert.Equal(companyId, body.RootElement.GetProperty("companyId").GetGuid());
        Assert.Equal("expense.vendor_picker", body.RootElement.GetProperty("context").GetString());
        Assert.Equal("vendor", body.RootElement.GetProperty("entityType").GetString());
        Assert.Equal("amazon", body.RootElement.GetProperty("query").GetString());
        Assert.Equal("select", body.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(entityId, body.RootElement.GetProperty("selectedEntityId").GetGuid());
        Assert.Equal(2, body.RootElement.GetProperty("rankPosition").GetInt32());
        Assert.Equal(8, body.RootElement.GetProperty("resultCount").GetInt32());
        Assert.Equal("/expenses/new", body.RootElement.GetProperty("sourceRoute").GetString());
        Assert.Equal(anchorId, body.RootElement.GetProperty("anchorEntityId").GetGuid());
    }

    [Fact]
    public async Task RecordUsageAsync_SwallowsExceptions()
    {
        var failing = new ThrowingHandler();
        using var http = new HttpClient(failing) { BaseAddress = new Uri("http://api.test/") };
        var service = new UnitySearchPickerService(http, NullLogger<UnitySearchPickerService>.Instance);

        // Should NOT throw — tracking failures are silent.
        await service.RecordUsageAsync(new UnitysearchUsageEvent
        {
            CompanyId = Guid.NewGuid(),
            Context = "ctx",
            EntityType = "vendor",
            EventType = "select",
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RecordUsageAsync_LogsButDoesNotThrowOnNonSuccess()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        var service = new UnitySearchPickerService(http, NullLogger<UnitySearchPickerService>.Instance);

        await service.RecordUsageAsync(new UnitysearchUsageEvent
        {
            CompanyId = Guid.NewGuid(),
            Context = "ctx",
            EntityType = "vendor",
            EventType = "no_match",
        }, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public CapturingHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("synthetic");
    }
}
