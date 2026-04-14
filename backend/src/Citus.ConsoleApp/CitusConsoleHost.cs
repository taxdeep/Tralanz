using Microsoft.Extensions.Configuration;
using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;

namespace Citus.ConsoleApp;

internal static class CitusConsoleHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = LoadConfiguration();
        var command = args.ElementAtOrDefault(0)?.Trim().ToLowerInvariant() ??
                      configuration["Console:DefaultCommand"]?.Trim().ToLowerInvariant() ??
                      "help";
        var commandArgs = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "help" or "--help" or "-h" => ShowHelp(),
                "describe-webvella" => DescribeWebVellaConsoleApp(),
                "health" => await RunAgainstDatabaseAsync(configuration, HealthAsync),
                "bootstrap-core" => await RunAgainstDatabaseAsync(configuration, BootstrapCoreAsync),
                "list-modules" => await RunAgainstDatabaseAsync(configuration, ListModulesAsync),
                "list-entities" => await RunAgainstDatabaseAsync(configuration, runtime => ListEntitiesAsync(runtime, commandArgs)),
                "show-entity" => await RunAgainstDatabaseAsync(configuration, runtime => ShowEntityAsync(runtime, commandArgs)),
                "upsert-demo-entity" => await RunAgainstDatabaseAsync(configuration, runtime => UpsertDemoEntityAsync(runtime, commandArgs)),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Command failed: {ex.Message}");
            return 1;
        }
    }

    private static IConfigurationRoot LoadConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Config.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

    private static async Task<int> RunAgainstDatabaseAsync(
        IConfiguration configuration,
        Func<CitusConsoleRuntime, Task<int>> command)
    {
        var connectionString =
            configuration.GetConnectionString("AccountingCore") ??
            configuration["CITUS_ACCOUNTING_DB"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore in Config.json or set CITUS_ACCOUNTING_DB.");
        }

        var runtime = new CitusConsoleRuntime(connectionString);
        return await command(runtime);
    }

    private static int ShowHelp()
    {
        Console.WriteLine("Citus.ConsoleApp");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  help");
        Console.WriteLine("  describe-webvella");
        Console.WriteLine("  health");
        Console.WriteLine("  bootstrap-core");
        Console.WriteLine("  list-modules");
        Console.WriteLine("  list-entities [moduleKey]");
        Console.WriteLine("  show-entity <entityName>");
        Console.WriteLine("  upsert-demo-entity [entityName]");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project backend/src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- describe-webvella");
        Console.WriteLine("  dotnet run --project backend/src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- bootstrap-core");
        Console.WriteLine("  dotnet run --project backend/src/Citus.ConsoleApp/Citus.ConsoleApp.csproj -- list-entities accounting");
        return 0;
    }

    private static int DescribeWebVellaConsoleApp()
    {
        Console.WriteLine("WebVella.Erp.ConsoleApp core control map");
        Console.WriteLine();
        Console.WriteLine("WebVella responsibilities:");
        Console.WriteLine("  InitErpEngine: initialize settings, DB context, AutoMapper, system entities, and hook registration.");
        Console.WriteLine("  SampleGetAllErpUsers: open a privileged security scope and run a controlled query.");
        Console.WriteLine("  RecordHookSample: demonstrate create/update/delete interception through hooks.");
        Console.WriteLine("  Config.json: keep console runtime self-contained.");
        Console.WriteLine();
        Console.WriteLine("Citus adaptation:");
        Console.WriteLine("  bootstrap-core: initialize the Citus platform kernel and seed built-in modules/entities.");
        Console.WriteLine("  health: verify database connectivity for the platform control plane.");
        Console.WriteLine("  list-modules / list-entities / show-entity: inspect the metadata registry that now plays the role of the core control surface.");
        Console.WriteLine("  upsert-demo-entity: demonstrate a controlled metadata mutation from the console app.");
        Console.WriteLine();
        Console.WriteLine("Why it differs:");
        Console.WriteLine("  WebVella.ConsoleApp controls a generic metadata+record engine.");
        Console.WriteLine("  Citus.ConsoleApp controls a platform kernel that governs bounded contexts, while accounting truth remains in the posting engine.");
        return 0;
    }

    private static async Task<int> HealthAsync(CitusConsoleRuntime runtime)
    {
        await using var connection = await runtime.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "select current_database(), current_user, version()";

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        await reader.ReadAsync(CancellationToken.None);

        Console.WriteLine("Database connectivity: ok");
        Console.WriteLine($"Database: {reader.GetString(0)}");
        Console.WriteLine($"User: {reader.GetString(1)}");
        Console.WriteLine($"Version: {reader.GetString(2)}");

        return 0;
    }

    private static async Task<int> BootstrapCoreAsync(CitusConsoleRuntime runtime)
    {
        var report = await runtime.Bootstrapper.BootstrapAsync(CancellationToken.None);

        Console.WriteLine("Platform core bootstrapped.");
        Console.WriteLine($"Modules seeded: {report.ModulesSeeded}");
        Console.WriteLine($"Entities seeded: {report.EntitiesSeeded}");
        Console.WriteLine($"Module keys: {string.Join(", ", report.ModuleKeys)}");

        return 0;
    }

    private static async Task<int> ListModulesAsync(CitusConsoleRuntime runtime)
    {
        await runtime.Repository.EnsureSchemaAsync(CancellationToken.None);
        var modules = await runtime.MetadataService.ListModulesAsync(CancellationToken.None);

        if (modules.Count == 0)
        {
            Console.WriteLine("No modules are registered. Run `bootstrap-core` first.");
            return 0;
        }

        foreach (var module in modules)
        {
            Console.WriteLine($"{module.Key} | {module.Name} | route={module.RoutePrefix} | entities={module.EntityNames.Count}");
        }

        return 0;
    }

    private static async Task<int> ListEntitiesAsync(CitusConsoleRuntime runtime, IReadOnlyList<string> args)
    {
        await runtime.Repository.EnsureSchemaAsync(CancellationToken.None);
        var entities = await runtime.MetadataService.ListEntitiesAsync(CancellationToken.None);

        if (args.Count > 0)
        {
            var moduleKey = args[0].Trim().ToLowerInvariant();
            entities = entities.Where(entity => entity.ModuleKey == moduleKey).ToArray();
        }

        if (entities.Count == 0)
        {
            Console.WriteLine("No entities matched the current filter. Run `bootstrap-core` first if the registry is empty.");
            return 0;
        }

        foreach (var entity in entities)
        {
            Console.WriteLine($"{entity.ModuleKey} | {entity.Name} | table={entity.StorageTable} | fields={entity.Fields.Count}");
        }

        return 0;
    }

    private static async Task<int> ShowEntityAsync(CitusConsoleRuntime runtime, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new InvalidOperationException("show-entity requires an entity name.");
        }

        await runtime.Repository.EnsureSchemaAsync(CancellationToken.None);
        var entityName = args[0].Trim().ToLowerInvariant();
        var entity = await runtime.MetadataService.GetEntityAsync(entityName, CancellationToken.None);

        if (entity is null)
        {
            Console.WriteLine($"Entity `{entityName}` is not registered.");
            return 0;
        }

        PrintEntity(entity);
        return 0;
    }

    private static async Task<int> UpsertDemoEntityAsync(CitusConsoleRuntime runtime, IReadOnlyList<string> args)
    {
        var entityName = args.Count > 0
            ? args[0].Trim().ToLowerInvariant()
            : "console_demo_entity";

        await runtime.Repository.EnsureSchemaAsync(CancellationToken.None);

        var entity = new CoreEntityDefinition
        {
            Id = Guid.NewGuid(),
            ModuleKey = PlatformModuleKeys.SysAdmin,
            Name = entityName,
            Label = "Console Demo Entity",
            LabelPlural = "Console Demo Entities",
            Description = "Created from Citus.ConsoleApp to demonstrate platform-core metadata control.",
            StorageTable = entityName,
            CompanyScoped = false,
            SystemScoped = true,
            Fields =
            [
                new CoreFieldDefinition
                {
                    Name = "id",
                    Label = "Id",
                    FieldType = "uuid",
                    SourceColumn = "id",
                    Required = true,
                    Searchable = true,
                    System = true
                },
                new CoreFieldDefinition
                {
                    Name = "display_name",
                    Label = "Display Name",
                    FieldType = "text",
                    SourceColumn = "display_name",
                    Required = true,
                    Searchable = true,
                    MaxLength = 240
                },
                new CoreFieldDefinition
                {
                    Name = "status",
                    Label = "Status",
                    FieldType = "text",
                    SourceColumn = "status",
                    Required = true,
                    Searchable = true,
                    MaxLength = 40
                }
            ],
            Permissions = new CoreEntityPermissionSet
            {
                Create = ["sysadmin"],
                Read = ["sysadmin"],
                Update = ["sysadmin"],
                Delete = ["sysadmin"]
            }
        };

        await runtime.MetadataService.UpsertEntityAsync(entity, CancellationToken.None);
        var stored = await runtime.MetadataService.GetEntityAsync(entityName, CancellationToken.None);

        Console.WriteLine("Demo entity upserted.");
        if (stored is not null)
        {
            PrintEntity(stored);
        }

        return 0;
    }

    private static void PrintEntity(CoreEntityDefinition entity)
    {
        Console.WriteLine($"{entity.Name} ({entity.Label})");
        Console.WriteLine($"Module: {entity.ModuleKey}");
        Console.WriteLine($"Table: {entity.StorageTable}");
        Console.WriteLine($"Company scoped: {entity.CompanyScoped}");
        Console.WriteLine($"System scoped: {entity.SystemScoped}");
        Console.WriteLine("Fields:");

        foreach (var field in entity.Fields)
        {
            Console.WriteLine($"  - {field.Name} | {field.FieldType} | source={field.SourceColumn} | required={field.Required} | searchable={field.Searchable}");
        }

        Console.WriteLine("Permissions:");
        Console.WriteLine($"  create: {JoinOrDash(entity.Permissions.Create)}");
        Console.WriteLine($"  read:   {JoinOrDash(entity.Permissions.Read)}");
        Console.WriteLine($"  update: {JoinOrDash(entity.Permissions.Update)}");
        Console.WriteLine($"  delete: {JoinOrDash(entity.Permissions.Delete)}");
    }

    private static string JoinOrDash(IReadOnlyList<string> values) =>
        values.Count == 0 ? "-" : string.Join(", ", values);

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run `help` to see available commands.");
        return 1;
    }
}
