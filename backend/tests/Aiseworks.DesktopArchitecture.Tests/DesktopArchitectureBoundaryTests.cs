using System.Xml.Linq;

namespace Aiseworks.DesktopArchitecture.Tests;

public sealed class DesktopArchitectureBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Desktop_shell_hosts_blazor_hybrid_and_shared_ui()
    {
        var project = LoadProject("src/Aiseworks.DesktopShell/Aiseworks.DesktopShell.csproj");
        var packageReferences = GetIncludeValues(project, "PackageReference").ToArray();
        var projectReferences = GetIncludeValues(project, "ProjectReference").ToArray();

        Assert.Contains("Microsoft.AspNetCore.Components.WebView.Wpf", packageReferences);
        Assert.Contains(projectReferences, value => value.EndsWith("Citus.Ui.Shared.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projectReferences, value => value.EndsWith("Aiseworks.ApiClient.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Desktop_shell_does_not_reference_server_owned_business_engines()
    {
        var project = LoadProject("src/Aiseworks.DesktopShell/Aiseworks.DesktopShell.csproj");
        var projectReferences = GetIncludeValues(project, "ProjectReference").ToArray();

        Assert.DoesNotContain(projectReferences, IsServerOwnedProjectReference);
    }

    [Fact]
    public void Shared_ui_and_api_client_stay_clear_of_server_implementation_projects()
    {
        var sharedUi = LoadProject("src/Citus.Ui.Shared/Citus.Ui.Shared.csproj");
        var apiClient = LoadProject("src/Aiseworks.ApiClient/Aiseworks.ApiClient.csproj");

        Assert.DoesNotContain(GetIncludeValues(sharedUi, "ProjectReference"), IsServerImplementationReference);
        Assert.DoesNotContain(GetIncludeValues(apiClient, "ProjectReference"), IsServerImplementationReference);
    }

    private static XDocument LoadProject(string relativePath)
    {
        return XDocument.Load(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static IEnumerable<string> GetIncludeValues(XDocument project, string elementName)
    {
        return project
            .Descendants(elementName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
    }

    private static bool IsServerOwnedProjectReference(string reference)
    {
        return IsServerImplementationReference(reference)
            || reference.Contains("Engines.", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Citus.Accounting.Application", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Modules\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServerImplementationReference(string reference)
    {
        return reference.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Citus.Accounting.Api", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Citus.SysAdmin.Api", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Citus.Accounting.Infrastructure", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Citus.Accounting.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate backend repository root.");
    }
}
