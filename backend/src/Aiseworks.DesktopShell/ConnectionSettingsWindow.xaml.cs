using System.Windows;
using System.Windows.Controls;

namespace Aiseworks.DesktopShell;

public partial class ConnectionSettingsWindow : Window
{
    private readonly bool _canEdit;

    public ConnectionSettingsDraft Result { get; private set; }

    public ConnectionSettingsWindow(
        ConnectionSettingsDraft draft,
        bool canEdit,
        string permissionText,
        string details)
    {
        InitializeComponent();

        _canEdit = canEdit;
        Result = draft;

        PermissionTextBlock.Text = permissionText;
        DetailsTextBox.Text = details;
        SaveButton.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = canEdit ? "Cancel" : "Close";

        HostTextBox.Text = draft.ServerHost;
        BusinessPortTextBox.Text = draft.BusinessPort.ToString();
        AccountingApiPortTextBox.Text = draft.AccountingApiPort.ToString();
        SysAdminPortTextBox.Text = draft.SysAdminPort.ToString();
        SysAdminApiPortTextBox.Text = draft.SysAdminApiPort.ToString();
        PostgresPortTextBox.Text = draft.PostgresPort.ToString();

        foreach (var textBox in EnumerateInputTextBoxes())
        {
            textBox.IsReadOnly = !canEdit;
            textBox.TextChanged += (_, _) => UpdateDerivedEndpoints();
        }

        UpdateDerivedEndpoints();
    }

    private IEnumerable<TextBox> EnumerateInputTextBoxes()
    {
        yield return HostTextBox;
        yield return BusinessPortTextBox;
        yield return AccountingApiPortTextBox;
        yield return SysAdminPortTextBox;
        yield return SysAdminApiPortTextBox;
        yield return PostgresPortTextBox;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_canEdit)
        {
            DialogResult = false;
            return;
        }

        if (!TryBuildDraft(out var draft, out var error))
        {
            ValidationTextBlock.Text = error;
            return;
        }

        Result = draft;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateDerivedEndpoints()
    {
        if (!TryBuildDraft(out var draft, out _))
        {
            DerivedEndpointsTextBlock.Text = "Enter a valid host and ports to preview endpoints.";
            return;
        }

        DerivedEndpointsTextBlock.Text = $"""
            Business UI:      http://{draft.ServerHost}:{draft.BusinessPort}
            Accounting API:   http://{draft.ServerHost}:{draft.AccountingApiPort}/health
            SysAdmin UI:      http://{draft.ServerHost}:{draft.SysAdminPort}
            SysAdmin API:     http://{draft.ServerHost}:{draft.SysAdminApiPort}/health
            Postgres:         {draft.ServerHost}:{draft.PostgresPort}
            """;
    }

    private bool TryBuildDraft(out ConnectionSettingsDraft draft, out string error)
    {
        draft = new ConnectionSettingsDraft();
        error = "";

        var host = HostTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Server IP / Host is required.";
            return false;
        }

        if (!TryParsePort(BusinessPortTextBox.Text, "Business UI port", out var businessPort, out error)
            || !TryParsePort(AccountingApiPortTextBox.Text, "Accounting API port", out var accountingApiPort, out error)
            || !TryParsePort(SysAdminPortTextBox.Text, "SysAdmin UI port", out var sysAdminPort, out error)
            || !TryParsePort(SysAdminApiPortTextBox.Text, "SysAdmin API port", out var sysAdminApiPort, out error)
            || !TryParsePort(PostgresPortTextBox.Text, "Postgres port", out var postgresPort, out error))
        {
            return false;
        }

        draft = new ConnectionSettingsDraft
        {
            ServerHost = host,
            BusinessPort = businessPort,
            AccountingApiPort = accountingApiPort,
            SysAdminPort = sysAdminPort,
            SysAdminApiPort = sysAdminApiPort,
            PostgresPort = postgresPort
        };
        return true;
    }

    private static bool TryParsePort(string raw, string label, out int port, out string error)
    {
        error = "";

        if (int.TryParse(raw.Trim(), out port) && port is >= 1 and <= 65535)
        {
            return true;
        }

        error = $"{label} must be a number between 1 and 65535.";
        return false;
    }
}
