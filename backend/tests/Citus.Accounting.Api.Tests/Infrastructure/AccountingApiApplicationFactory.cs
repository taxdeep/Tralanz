using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Citus.Accounting.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real Citus.Accounting.Api host in-memory for host-level smoke
/// and route-snapshot tests.
///
/// Two test-only accommodations, neither of which alters a production code
/// path: (1) the startup runtime-DDL bootstrap is disabled via config
/// (<c>SchemaManagement:ApplyOnStartup=false</c>) so the host builds without a
/// live PostgreSQL; (2) <see cref="IPlatformRuntimeStateRepository"/> is
/// replaced with an in-memory fake so the <c>/accounting</c> group guard's
/// per-request maintenance-state lookup can run without a DB.
///
/// Building this host exercises the DI graph end to end — with
/// <c>ValidateScopes</c> + <c>ValidateOnBuild</c> forced on in Program.cs, a
/// captive dependency or unconstructable registration fails here.
/// </summary>
public sealed class AccountingApiApplicationFactory : WebApplicationFactory<global::Program>
{
    public FakePlatformRuntimeStateRepository RuntimeState { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(
            [
                // Non-empty so the Program.cs connection-string guard passes.
                // Never actually opened: the bootstrap is disabled below and
                // the Npgsql-backed stores are constructed lazily.
                KeyValuePair.Create<string, string?>(
                    "CITUS_ACCOUNTING_DB",
                    "Host=127.0.0.1;Port=5432;Database=citus_route_snapshot;Username=postgres;Password=postgres"),
                // Skip the startup EnsureSchemaAsync DDL block so no DB is hit.
                KeyValuePair.Create<string, string?>(
                    "SchemaManagement:ApplyOnStartup",
                    bool.FalseString),
            ]);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPlatformRuntimeStateRepository>();
            services.AddSingleton<IPlatformRuntimeStateRepository>(RuntimeState);
        });
    }
}

/// <summary>
/// In-memory <see cref="IPlatformRuntimeStateRepository"/> for host tests.
/// Only <see cref="GetMaintenanceStateAsync"/> is meaningful (the
/// <c>/accounting</c> group guard calls it on every request); the remaining
/// members echo or return defaults.
/// </summary>
public sealed class FakePlatformRuntimeStateRepository : IPlatformRuntimeStateRepository
{
    public PlatformMaintenanceState? MaintenanceState { get; set; }

    public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<PlatformMaintenanceState?> GetMaintenanceStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(MaintenanceState);

    public Task<PlatformMaintenanceState> UpsertMaintenanceStateAsync(
        PlatformMaintenanceState state,
        CancellationToken cancellationToken) =>
        Task.FromResult(state);

    public Task<PlatformNotificationReadinessState?> GetNotificationReadinessStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PlatformNotificationReadinessState?>(null);

    public Task<PlatformNotificationReadinessState> UpsertNotificationReadinessStateAsync(
        PlatformNotificationReadinessState state,
        CancellationToken cancellationToken) =>
        Task.FromResult(state);

    public Task<PlatformFirstCompanySetupState?> GetFirstCompanySetupStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PlatformFirstCompanySetupState?>(null);

    public Task<PlatformFirstCompanySetupState> UpsertFirstCompanySetupStateAsync(
        PlatformFirstCompanySetupState state,
        CancellationToken cancellationToken) =>
        Task.FromResult(state);
}
