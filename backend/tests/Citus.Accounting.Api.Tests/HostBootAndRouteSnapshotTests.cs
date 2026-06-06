using System.Net;
using System.Runtime.CompilerServices;
using Citus.Accounting.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Citus.Accounting.Api.Tests;

/// <summary>
/// Refactor safety net for the staged Program.cs decomposition. These tests
/// boot the real Accounting.Api host in-memory and assert that the public
/// surface — the full set of (HTTP method, route) pairs — is byte-for-byte
/// stable, and that the DI graph builds with scope + build validation on.
///
/// During endpoint extraction (P6) the snapshot is the structural guard: it
/// catches a dropped, renamed, duplicated, or re-verbed endpoint immediately.
/// Permission/rate-limit gates are endpoint *filters* (not metadata), so they
/// are conserved by moving whole handler statements intact and cross-checked
/// with a source-level invariant during each move.
/// </summary>
public sealed class HostBootAndRouteSnapshotTests
{
    [Fact]
    public void Host_builds_with_di_scope_and_build_validation()
    {
        using var factory = new AccountingApiApplicationFactory();

        // Resolving any service forces the host to build, which runs
        // ValidateScopes + ValidateOnBuild (forced on in Program.cs).
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        Assert.NotNull(dataSource);
        Assert.NotEmpty(dataSource.Endpoints);
    }

    [Fact]
    public void Route_table_matches_committed_baseline()
    {
        using var factory = new AccountingApiApplicationFactory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var snapshot = BuildSnapshot(dataSource);
        var baselinePath = BaselinePath();

        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath, snapshot);
            Assert.Fail(
                $"Route baseline did not exist; wrote {CountLines(snapshot)} routes to " +
                $"'{baselinePath}'. Inspect, commit it, then re-run to compare.");
        }

        var expected = Normalize(File.ReadAllText(baselinePath));
        var actual = Normalize(snapshot);

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            // Drop the current table next to the baseline for an easy diff.
            File.WriteAllText(baselinePath + ".actual", snapshot);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Unauthenticated_accounting_request_is_rejected_by_group_guard()
    {
        using var factory = new AccountingApiApplicationFactory();
        using var client = factory.CreateClient();

        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // Pick a stable, parameterless GET under /accounting so the call is
        // route-rename resilient and needs no body/route args.
        var route = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .StartsWith("/accounting", StringComparison.Ordinal))
            .Where(e => !(e.RoutePattern.RawText ?? string.Empty).Contains('{'))
            .Where(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Contains("GET"))
            .Select(e => e.RoutePattern.RawText!)
            .OrderBy(r => r, StringComparer.Ordinal)
            .First();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string BuildSnapshot(EndpointDataSource dataSource)
    {
        var lines = new List<string>();

        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            var verb = methods is { Count: > 0 }
                ? string.Join(",", methods.OrderBy(m => m, StringComparer.Ordinal))
                : "ANY";
            var route = endpoint.RoutePattern.RawText ?? "(null)";
            lines.Add($"{verb} {route}");
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").TrimEnd('\n');

    private static int CountLines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string BaselinePath([CallerFilePath] string? thisFile = null) =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "RouteSnapshot.baseline.txt");
}
