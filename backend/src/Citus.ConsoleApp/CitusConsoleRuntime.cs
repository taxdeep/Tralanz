using Citus.Platform.Core.Services;
using Citus.Platform.Infrastructure.Persistence;

namespace Citus.ConsoleApp;

internal sealed class CitusConsoleRuntime
{
    public CitusConsoleRuntime(string connectionString)
    {
        ConnectionString = connectionString;
        ConnectionFactory = new PlatformPostgresConnectionFactory(connectionString);
        Repository = new PostgresPlatformMetadataRepository(ConnectionFactory);
        MetadataService = new PlatformMetadataService(Repository);
        Bootstrapper = new PlatformCoreBootstrapper(Repository, MetadataService);
    }

    public string ConnectionString { get; }

    public PlatformPostgresConnectionFactory ConnectionFactory { get; }

    public PostgresPlatformMetadataRepository Repository { get; }

    public PlatformMetadataService MetadataService { get; }

    public PlatformCoreBootstrapper Bootstrapper { get; }
}
