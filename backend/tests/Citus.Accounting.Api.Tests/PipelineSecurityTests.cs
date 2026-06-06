using System.Net;
using Citus.Accounting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Citus.Accounting.Api.Tests;

/// <summary>
/// P5 verification: the extracted middleware pipeline still serves requests,
/// stamps the conservative security headers on every response, and the
/// /internal/* network guard blocks when enforcement is enabled.
/// </summary>
public sealed class PipelineSecurityTests
{
    [Fact]
    public async Task Security_headers_present_on_anonymous_response()
    {
        using var factory = new AccountingApiApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.NotNull(Header(response, "Permissions-Policy"));
    }

    [Fact]
    public async Task Security_headers_present_on_unauthenticated_accounting_response()
    {
        using var factory = new AccountingApiApplicationFactory();
        using var client = factory.CreateClient();

        var route = FirstParameterlessAccountingGet(factory);
        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Internal_endpoints_blocked_when_enforcement_enabled()
    {
        using var factory = new AccountingApiApplicationFactory()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(
                [
                    KeyValuePair.Create<string, string?>("InternalEndpoints:Enforce", "true"),
                ])));
        using var client = factory.CreateClient();

        // The guard runs in the pipeline before routing, so any verb/path under
        // /internal is gated. The TestServer connection is not a permitted
        // (loopback/allow-listed) origin, so enforcement returns 403.
        var response = await client.GetAsync("/internal/ai/distill-unitysearch");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string FirstParameterlessAccountingGet(AccountingApiApplicationFactory factory)
    {
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        return dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("/accounting", StringComparison.Ordinal))
            .Where(e => !(e.RoutePattern.RawText ?? string.Empty).Contains('{'))
            .Where(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains("GET"))
            .Select(e => e.RoutePattern.RawText!)
            .OrderBy(r => r, StringComparer.Ordinal)
            .First();
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues(name, out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
    }
}
