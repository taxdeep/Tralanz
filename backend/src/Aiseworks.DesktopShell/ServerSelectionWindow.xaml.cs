using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Aiseworks.DesktopShell;

public partial class ServerSelectionWindow : Window
{
    private readonly ConnectionSettingsState _fallback;

    public ServerSelectionWindow(ConnectionSettingsState fallback)
    {
        InitializeComponent();
        _fallback = fallback;

        var recentServers = BuildRecentServers(fallback).ToList();
        ServerComboBox.ItemsSource = recentServers;
        ServerComboBox.Text = recentServers.FirstOrDefault() ?? FormatServer(fallback);
        ServerComboBox.Focus();
    }

    public ConnectionSettingsState Result { get; private set; } = new();

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result, out var error))
        {
            ErrorTextBlock.Text = error;
            return;
        }

        Result = result;
        DialogResult = true;
    }

    private void ServerComboBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConnectButton_Click(sender, e);
        }
    }

    private bool TryBuildResult(out ConnectionSettingsState result, out string error)
    {
        result = new ConnectionSettingsState();
        error = string.Empty;

        var input = ServerComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Server host is required.";
            return false;
        }

        if (!TryParseServerInput(input, _fallback, out var host, out var businessPort))
        {
            error = "Enter a valid server host, IP, or host:port.";
            return false;
        }

        result = new ConnectionSettingsState
        {
            ServerHost = host,
            BusinessPort = businessPort,
            AccountingApiPort = _fallback.AccountingApiPort,
            SysAdminPort = _fallback.SysAdminPort,
            SysAdminApiPort = _fallback.SysAdminApiPort,
            PostgresPort = _fallback.PostgresPort,
            RecentServers = BuildUpdatedRecentServers(FormatServer(host, businessPort), _fallback.RecentServers),
            UpdatedAt = DateTimeOffset.Now,
            UpdatedBy = "Desktop startup"
        };

        if (!result.IsValid())
        {
            error = "Server settings are incomplete.";
            return false;
        }

        return true;
    }

    private static bool TryParseServerInput(
        string input,
        ConnectionSettingsState fallback,
        out string host,
        out int businessPort)
    {
        host = string.Empty;
        businessPort = fallback.BusinessPort;

        var candidate = input.Contains("://", StringComparison.Ordinal)
            ? input
            : $"http://{input}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        host = uri.Host.Trim();
        if (HasExplicitPort(input))
        {
            businessPort = uri.Port;
        }

        return businessPort is >= 1 and <= 65535;
    }

    private static bool HasExplicitPort(string input)
    {
        var withoutScheme = input.Contains("://", StringComparison.Ordinal)
            ? input[(input.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : input;
        var slashIndex = withoutScheme.IndexOf('/');
        if (slashIndex >= 0)
        {
            withoutScheme = withoutScheme[..slashIndex];
        }

        var colonIndex = withoutScheme.LastIndexOf(':');
        return colonIndex >= 0
            && colonIndex < withoutScheme.Length - 1
            && int.TryParse(withoutScheme[(colonIndex + 1)..], out _);
    }

    private static IEnumerable<string> BuildRecentServers(ConnectionSettingsState fallback)
    {
        yield return FormatServer(fallback);

        foreach (var server in fallback.RecentServers)
        {
            if (!string.IsNullOrWhiteSpace(server))
            {
                yield return server.Trim();
            }
        }
    }

    private static List<string> BuildUpdatedRecentServers(string selectedServer, IEnumerable<string> existing)
    {
        return new[] { selectedServer }
            .Concat(existing)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string FormatServer(ConnectionSettingsState settings) =>
        FormatServer(settings.ServerHost, settings.BusinessPort);

    private static string FormatServer(string host, int businessPort) =>
        businessPort == 18080 ? host : $"{host}:{businessPort}";
}
