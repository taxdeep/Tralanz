using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Modules;
using Citus.Platform.Core.Services;
using Citus.Platform.Infrastructure.Persistence;
using Citus.SysAdmin.Api.Control;
using Citus.SysAdmin.Api;
using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Shell;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("AccountingCore") ??
    builder.Configuration["CITUS_ACCOUNTING_DB"];

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");
}

builder.Services.AddSingleton(new PlatformPostgresConnectionFactory(connectionString));
builder.Services.AddScoped<IPlatformMetadataRepository, PostgresPlatformMetadataRepository>();
builder.Services.AddScoped<IPlatformMetadataService, PlatformMetadataService>();
builder.Services.AddScoped<IPlatformBootstrapper, PlatformCoreBootstrapper>();
builder.Services.AddSingleton<IPlatformRuntimeStateRepository, PostgresPlatformRuntimeStateRepository>();
builder.Services.Configure<SysAdminControlOptions>(builder.Configuration.GetSection(SysAdminControlOptions.SectionName));
builder.Services.AddSingleton<SysAdminControlState>();

var app = builder.Build();

await using (var startupScope = app.Services.CreateAsyncScope())
{
    var runtimeRepository = startupScope.ServiceProvider.GetRequiredService<IPlatformRuntimeStateRepository>();
    var controlState = startupScope.ServiceProvider.GetRequiredService<SysAdminControlState>();

    await runtimeRepository.EnsureSchemaAsync(CancellationToken.None);

    var persistedMaintenance = await runtimeRepository.GetMaintenanceStateAsync(CancellationToken.None);
    if (persistedMaintenance is null)
    {
        await runtimeRepository.UpsertMaintenanceStateAsync(
            controlState.GetMaintenanceState().ToPlatformMaintenanceState(),
            CancellationToken.None);
    }
    else
    {
        controlState.SetMaintenanceState(persistedMaintenance.ToSummary());
    }
}

app.MapGet("/", () => Results.Ok(new
{
    service = "Citus.SysAdmin.Api",
    status = "platform-core-wired",
    purpose = "system administration and platform core control",
    core = "Citus.Platform.Core"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Citus.SysAdmin.Api",
    utc = DateTimeOffset.UtcNow
}));

var core = app.MapGroup("/core");
var control = app.MapGroup("/control");

core.MapGet(
    "/",
    async (IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var modules = await service.ListModulesAsync(cancellationToken);
        var entities = await service.ListEntitiesAsync(cancellationToken);

        return Results.Ok(new
        {
            name = "Citus.Platform.Core",
            inspiration = "WebVella-style metadata-driven ERP kernel adapted for Citus",
            modulesRegistered = modules.Count,
            entitiesRegistered = entities.Count,
            capabilities = new[]
            {
                "bootstrap",
                "module-registry",
                "entity-metadata"
            }
        });
    });

core.MapPost(
    "/bootstrap",
    async (IPlatformBootstrapper bootstrapper, CancellationToken cancellationToken) =>
    {
        var report = await bootstrapper.BootstrapAsync(cancellationToken);
        return Results.Ok(report);
    });

core.MapGet(
    "/modules",
    async (IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var modules = await service.ListModulesAsync(cancellationToken);

        return Results.Ok(modules.Select(module => new
        {
            module.Id,
            module.Key,
            module.Name,
            module.Description,
            module.RoutePrefix,
            module.IsSystemModule,
            module.Capabilities,
            module.EntityNames
        }));
    });

core.MapGet(
    "/entities",
    async (IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var entities = await service.ListEntitiesAsync(cancellationToken);

        return Results.Ok(entities.Select(entity => new
        {
            entity.Id,
            entity.ModuleKey,
            entity.Name,
            entity.Label,
            entity.LabelPlural,
            entity.StorageTable,
            entity.CompanyScoped,
            entity.SystemScoped,
            FieldCount = entity.Fields.Count
        }));
    });

core.MapGet(
    "/entities/{name}",
    async (string name, IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);
        var entity = await service.GetEntityAsync(name, cancellationToken);

        return entity is null
            ? Results.NotFound(new
            {
                message = $"Entity '{name}' is not registered in the platform core."
            })
            : Results.Ok(entity);
    });

core.MapPost(
    "/entities",
    async (UpsertCoreEntityHttpRequest request, IPlatformMetadataRepository repository, IPlatformMetadataService service, CancellationToken cancellationToken) =>
    {
        await repository.EnsureSchemaAsync(cancellationToken);

        try
        {
            var entityDefinition = request.ToEntityDefinition();
            await service.UpsertEntityAsync(entityDefinition, cancellationToken);
            var stored = await service.GetEntityAsync(entityDefinition.Name, cancellationToken);
            return Results.Ok(stored);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                message = ex.Message
            });
        }
    });

app.MapGet("/modules/accounting", () => Results.Ok(new
{
    key = PlatformModuleKeys.Accounting,
    status = "registered-through-platform-core",
    route = "/accounting"
}));

control.MapGet(
    "/context",
    async (SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
    {
        var persistedMaintenance = await GetMaintenanceStateAsync(runtimeRepository, state, cancellationToken);
        return Results.Ok(state.GetContext() with
        {
            MaintenanceState = persistedMaintenance
        });
    });

control.MapGet(
    "/companies",
    (SysAdminControlState state) => Results.Ok(state.GetCompanies()));

control.MapGet(
    "/users",
    (SysAdminControlState state) => Results.Ok(state.GetUsers()));

control.MapGet(
    "/maintenance",
    async (SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
        Results.Ok(await GetMaintenanceStateAsync(runtimeRepository, state, cancellationToken)));

control.MapPut(
    "/active-company/{companyId:guid}",
    async (Guid companyId, SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
    {
        if (!state.TrySetActiveCompany(companyId, out var context))
        {
            return Results.NotFound(new
            {
                message = $"Company '{companyId}' is not managed by the SysAdmin control context."
            });
        }

        var maintenanceState = await GetMaintenanceStateAsync(runtimeRepository, state, cancellationToken);

        return Results.Ok(context with
        {
            MaintenanceState = maintenanceState
        });
    });

control.MapPut(
    "/maintenance",
    async (MaintenanceUpdateRequest request, SysAdminControlState state, IPlatformRuntimeStateRepository runtimeRepository, CancellationToken cancellationToken) =>
    {
        var updatedState = state.UpdateMaintenance(request);
        var persistedState = await runtimeRepository.UpsertMaintenanceStateAsync(
            updatedState.ToPlatformMaintenanceState(),
            cancellationToken);

        state.SetMaintenanceState(persistedState.ToSummary());
        return Results.Ok(persistedState.ToSummary());
    });

app.Run();

static async Task<MaintenanceStateSummary> GetMaintenanceStateAsync(
    IPlatformRuntimeStateRepository runtimeRepository,
    SysAdminControlState controlState,
    CancellationToken cancellationToken)
{
    var persistedState = await runtimeRepository.GetMaintenanceStateAsync(cancellationToken);

    if (persistedState is null)
    {
        return controlState.GetMaintenanceState();
    }

    var summary = persistedState.ToSummary();
    controlState.SetMaintenanceState(summary);
    return summary;
}
