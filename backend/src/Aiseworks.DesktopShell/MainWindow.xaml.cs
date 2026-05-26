using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Aiseworks.DesktopShell;

public partial class MainWindow : Window
{
    private const string DesktopBridgeChannel = "aiseworks.desktopBridge";

    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly JsonSerializerOptions DesktopBridgeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ShellOptions _options;
    private readonly IReadOnlyList<ServerProfileOptions> _serverProfiles;
    private readonly string _repositoryRoot;
    private readonly string _stateFilePath;
    private ServerProfileOptions _serverProfile;
    private IReadOnlyList<HealthCheckResult> _lastHealthResults = [];
    private ServiceProbeResult _lastServiceProbe = ServiceProbeResult.NotApplicable();
    private DateTimeOffset? _lastHealthCheckedAt;
    private string? _lastBackupFile;
    private BackupState _backupState = new();
    private bool _showingErrorPage;
    private int _desktopBridgeSequence;
    private bool _desktopBridgeReady;

    public MainWindow()
    {
        InitializeComponent();
        _options = LoadOptions();
        _serverProfiles = ResolveServerProfiles(_options);
        _serverProfile = ResolveServerProfile(_options, _serverProfiles);
        _repositoryRoot = ResolveRepositoryRoot(_options);
        _stateFilePath = ResolveStateFilePath();
        ConfigureServerProfileSelector();
        ApplyProfileCapabilities();
        LoadBackupState();
        Loaded += MainWindow_Loaded;
    }

    private static ShellOptions LoadOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(path))
        {
            return new ShellOptions();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ShellOptions>(json) ?? new ShellOptions();
        }
        catch (JsonException)
        {
            return new ShellOptions();
        }
    }

    private static IReadOnlyList<ServerProfileOptions> ResolveServerProfiles(ShellOptions options)
    {
        if (options.ServerProfiles.Count > 0)
        {
            return options.ServerProfiles;
        }

        return
        [
            new ServerProfileOptions
            {
                Name = string.IsNullOrWhiteSpace(options.SelectedServerProfileName)
                    ? "Legacy Local Settings"
                    : options.SelectedServerProfileName,
                Description = "Compatibility profile built from legacy flat desktop shell settings.",
                ServiceName = "",
                BusinessUrl = options.BusinessUrl,
                SysAdminUrl = options.SysAdminUrl,
                StartupUrl = options.StartupUrl,
                AccountingApiHealthUrl = options.AccountingApiHealthUrl,
                BusinessHealthUrl = options.BusinessHealthUrl,
                SysAdminApiHealthUrl = options.SysAdminApiHealthUrl,
                SysAdminHealthUrl = options.SysAdminHealthUrl,
                PostgresHost = options.PostgresHost,
                PostgresPort = options.PostgresPort,
                Kind = ServerProfileKinds.DockerTestStack
            }
        ];
    }

    private static ServerProfileOptions ResolveServerProfile(
        ShellOptions options,
        IReadOnlyList<ServerProfileOptions> profiles)
    {
        return profiles.FirstOrDefault(item =>
            string.Equals(item.Name, options.SelectedServerProfileName, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
    }

    private void ConfigureServerProfileSelector()
    {
        ServerProfileComboBox.ItemsSource = _serverProfiles;
        ServerProfileComboBox.SelectedItem = _serverProfile;
        ServerProfileComboBox.IsEnabled = _serverProfiles.Count > 1;
        ServerProfileComboBox.ToolTip = _serverProfiles.Count > 1
            ? "Select the Aiseworks server profile for this desktop session"
            : "Only one server profile is configured";
    }

    private void ApplyActiveServerProfile()
    {
        _lastHealthResults = [];
        _lastServiceProbe = ServiceProbeResult.NotApplicable();
        _lastHealthCheckedAt = null;
        _desktopBridgeReady = false;
        StatusTextBlock.Text = "Profile selected";
        SetBridgeStatus("Waiting", null);
        DockerStatusTextBlock.Text = IsDockerTestStackProfile()
            ? "Docker: not checked"
            : "Server: not checked";
        SetHealthSummary("Unknown", null);
        PostgresHealthTextBlock.Text = "Unknown";
        AccountingApiHealthTextBlock.Text = "Unknown";
        BusinessHealthTextBlock.Text = "Unknown";
        SysAdminApiHealthTextBlock.Text = "Unknown";
        SysAdminHealthTextBlock.Text = "Unknown";
        ServiceStatusTextBlock.Text = "N/A";
        ApplyProfileCapabilities();
    }

    private async void ServerProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerProfileComboBox.SelectedItem is not ServerProfileOptions profile
            || ReferenceEquals(profile, _serverProfile))
        {
            return;
        }

        _serverProfile = profile;
        ApplyActiveServerProfile();

        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        Navigate(NormalizeUrl(_serverProfile.StartupUrl));
        await CheckServicesAsync();
    }

    private static string ResolveRepositoryRoot(ShellOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RepositoryRoot)
            && Directory.Exists(options.RepositoryRoot))
        {
            return Path.GetFullPath(options.RepositoryRoot);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "deploy", "docker", "compose.yml")))
            {
                return directory.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveStateFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aiseworks",
            "DesktopShell");

        return Path.Combine(directory, "backup-state.json");
    }

    private void ApplyProfileCapabilities()
    {
        ProfileModeTextBlock.Text = GetProfileModeText();
        ProfileModeTextBlock.ToolTip = GetServiceManagementStatus();
        ApplyServiceProbeResult(ServiceProbeResult.NotChecked(GetServiceTargetText()));
        SetServerControlButtonsEnabled(true);
        StartStackButton.ToolTip = GetServerControlTooltip("start");
        StopStackButton.ToolTip = GetServerControlTooltip("stop");
        RestartStackButton.ToolTip = GetServerControlTooltip("restart");
        StartServerMenuItem.ToolTip = StartStackButton.ToolTip;
        StopServerMenuItem.ToolTip = StopStackButton.ToolTip;
        RestartServerMenuItem.ToolTip = RestartStackButton.ToolTip;
        ShowLogsButton.ToolTip = IsDockerTestStackProfile()
            ? "Show recent Docker test-stack logs"
            : "Only available for the Docker test-stack profile";
        ShowLogsMenuItem.ToolTip = ShowLogsButton.ToolTip;
        BackupDatabaseButton.ToolTip = IsDockerTestStackProfile()
            ? "Create a PostgreSQL backup from the local Docker test database"
            : "Only available for the Docker test-stack profile";
        BackupDatabaseMenuItem.ToolTip = BackupDatabaseButton.ToolTip;
        ConnectionDetailsButton.ToolTip = "Show the active server profile, health targets, and enabled desktop actions";
    }

    private void LoadBackupState()
    {
        if (!File.Exists(_stateFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_stateFilePath);
            _backupState = JsonSerializer.Deserialize<BackupState>(json) ?? new BackupState();
            _lastBackupFile = _backupState.LastBackupFile;
            UpdateLastBackupText();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _backupState = new BackupState();
            _lastBackupFile = null;
        }
    }

    private void SaveBackupState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                _backupState,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowDiagnostics(
                "Aiseworks backup state not saved",
                $"Backup completed, but the desktop shell could not save local backup state at '{_stateFilePath}': {ex.Message}");
        }
    }

    private void UpdateLastBackupText()
    {
        if (string.IsNullOrWhiteSpace(_backupState.LastBackupFile)
            || !_backupState.LastBackupCompletedAt.HasValue
            || !_backupState.LastBackupSizeBytes.HasValue)
        {
            LastBackupTextBlock.Text = "Backup: none";
            return;
        }

        LastBackupTextBlock.Text =
            $"Backup: {_backupState.LastBackupSizeBytes.Value:N0} bytes at {_backupState.LastBackupCompletedAt.Value:yyyy-MM-dd HH:mm}";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
            Browser.CoreWebView2.StatusBarTextChanged += CoreWebView2_StatusBarTextChanged;
            Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;

            Navigate(NormalizeUrl(_serverProfile.StartupUrl));
            await CheckServicesAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusTextBlock.Text = "WebView2 runtime missing";
            MessageBox.Show(
                "Aiseworks Desktop Shell requires Microsoft Edge WebView2 Runtime.",
                "Aiseworks",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (InvalidOperationException ex)
        {
            StatusTextBlock.Text = "Desktop shell failed";
            MessageBox.Show(ex.Message, "Aiseworks", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _showingErrorPage = false;
        _desktopBridgeReady = false;
        AddressTextBox.Text = e.Uri;
        StatusTextBlock.Text = "Loading";
        WebStatusTextBlock.Text = e.Uri;
        SetBridgeStatus("Loading", null);
        UpdateNavigationButtons();
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateNavigationButtons();

        if (e.IsSuccess)
        {
            StatusTextBlock.Text = "Ready";
            WebStatusTextBlock.Text = "Ready";
            return;
        }

        if (_showingErrorPage)
        {
            return;
        }

        _showingErrorPage = true;
        StatusTextBlock.Text = "Server unavailable";
        WebStatusTextBlock.Text = $"Navigation failed: {e.WebErrorStatus}";
        Browser.NavigateToString(BuildServerUnavailablePage(e.WebErrorStatus));
    }

    private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
    {
        var title = Browser.CoreWebView2.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(title) ? "Aiseworks" : $"Aiseworks - {title}";
    }

    private void CoreWebView2_StatusBarTextChanged(object? sender, object e)
    {
        var value = Browser.CoreWebView2.StatusBarText;
        WebStatusTextBlock.Text = string.IsNullOrWhiteSpace(value)
            ? StatusTextBlock.Text
            : value;
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        DesktopBridgeMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<DesktopBridgeMessage>(
                e.WebMessageAsJson,
                DesktopBridgeJsonOptions);
        }
        catch (JsonException ex)
        {
            SetBridgeStatus("Invalid message", false, ex.Message);
            return;
        }

        if (message is null
            || !string.Equals(message.Channel, DesktopBridgeChannel, StringComparison.Ordinal)
            || !string.Equals(message.Direction, "web-to-shell", StringComparison.Ordinal))
        {
            return;
        }

        HandleDesktopBridgeMessage(message);
    }

    private void HandleDesktopBridgeMessage(DesktopBridgeMessage message)
    {
        switch (message.Type)
        {
            case "web.ready":
                _desktopBridgeReady = true;
                SetBridgeStatus("Ready", true, FormatBridgeTooltip(message));
                SendDesktopBridgeCommand("shell.context", BuildDesktopBridgeContextPayload());
                break;

            case "web.pong":
                _desktopBridgeReady = true;
                SetBridgeStatus("Pong received", true, FormatBridgeTooltip(message));
                break;

            case "web.context.received":
                SetBridgeStatus("Context acknowledged", true, FormatBridgeTooltip(message));
                break;

            case "web.request.systemStatus":
                SendDesktopBridgeCommand(
                    "shell.response",
                    BuildDesktopBridgeContextPayload(),
                    message.Id);
                SetBridgeStatus("Status requested", true, FormatBridgeTooltip(message));
                break;

            default:
                SetBridgeStatus($"Web event: {message.Type}", true, FormatBridgeTooltip(message));
                break;
        }
    }

    private void SendDesktopBridgeCommand(string type, object? payload = null, string? replyTo = null)
    {
        if (Browser.CoreWebView2 is null)
        {
            SetBridgeStatus("Not initialized", false, "WebView2 is not initialized yet.");
            return;
        }

        var message = new DesktopBridgeOutgoingMessage(
            Channel: DesktopBridgeChannel,
            Direction: "shell-to-web",
            Type: type,
            Id: NextDesktopBridgeMessageId(),
            SentAt: DateTimeOffset.UtcNow,
            Payload: payload,
            ReplyTo: replyTo);

        try
        {
            Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, DesktopBridgeJsonOptions));
            SetBridgeStatus(_desktopBridgeReady ? "Command sent" : "Command sent; waiting for Web", null);
        }
        catch (ArgumentException ex)
        {
            SetBridgeStatus("Send failed", false, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            SetBridgeStatus("Send failed", false, ex.Message);
        }
    }

    private string NextDesktopBridgeMessageId()
    {
        var sequence = Interlocked.Increment(ref _desktopBridgeSequence);
        return $"shell-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{sequence}";
    }

    private object BuildDesktopBridgeContextPayload()
    {
        return new
        {
            profile = _serverProfile.Name,
            mode = GetProfileModeText().Replace("Mode: ", "", StringComparison.Ordinal),
            currentUrl = Browser.Source?.ToString() ?? AddressTextBox.Text,
            title = Title,
            checkedAt = _lastHealthCheckedAt,
            health = BuildSystemStatusSnapshot()
        };
    }

    private void SetBridgeStatus(string value, bool? healthy, string? tooltip = null)
    {
        BridgeStatusTextBlock.Text = value;
        BridgeStatusTextBlock.ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip;
        BridgeStatusTextBlock.Foreground = healthy switch
        {
            true => new SolidColorBrush(Color.FromRgb(5, 122, 85)),
            false => new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            _ => new SolidColorBrush(Color.FromRgb(107, 114, 128))
        };
    }

    private static string FormatBridgeTooltip(DesktopBridgeMessage message)
    {
        return string.IsNullOrWhiteSpace(message.Id)
            ? message.Type
            : $"{message.Type} ({message.Id})";
    }

    private void Navigate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusTextBlock.Text = "Invalid address";
            return;
        }

        AddressTextBox.Text = uri.ToString();
        Browser.Source = uri;
    }

    private static string NormalizeUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"http://{url}";
    }

    private string BuildBusinessRouteUrl(string relativePath)
    {
        var baseUrl = NormalizeUrl(_serverProfile.BusinessUrl);
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, relativePath.TrimStart('/')).ToString();
    }

    private string BuildServerUnavailablePage(CoreWebView2WebErrorStatus status)
    {
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <title>Aiseworks</title>
              <style>
                body {
                  margin: 0;
                  font-family: "Segoe UI", Arial, sans-serif;
                  background: #f7f8fa;
                  color: #1f2937;
                }
                main {
                  max-width: 760px;
                  margin: 96px auto;
                  padding: 0 24px;
                }
                h1 {
                  margin: 0 0 12px;
                  font-size: 28px;
                  font-weight: 650;
                }
                p {
                  line-height: 1.55;
                  font-size: 15px;
                }
                code {
                  background: #eef2f7;
                  border: 1px solid #d6dae0;
                  border-radius: 4px;
                  padding: 2px 5px;
                }
                .panel {
                  margin-top: 22px;
                  padding: 18px;
                  border: 1px solid #d6dae0;
                  border-radius: 8px;
                  background: #fff;
                }
              </style>
            </head>
            <body>
              <main>
                <h1>Aiseworks Server is not reachable</h1>
                <p>The desktop shell is running, but it could not open the local Aiseworks web application.</p>
                <div class="panel">
                  <p>Connection profile: <code>{{WebUtility.HtmlEncode(_serverProfile.Name)}}</code></p>
                  <p>Expected Business UI: <code>{{WebUtility.HtmlEncode(_serverProfile.BusinessUrl)}}</code></p>
                  <p>Expected SysAdmin UI: <code>{{WebUtility.HtmlEncode(_serverProfile.SysAdminUrl)}}</code></p>
                  <p>Navigation status: <code>{{WebUtility.HtmlEncode(status.ToString())}}</code></p>
                </div>
              </main>
            </body>
            </html>
            """;
    }

    private void UpdateNavigationButtons()
    {
        var canGoBack = Browser.CanGoBack;
        var canGoForward = Browser.CanGoForward;
        BackButton.IsEnabled = canGoBack;
        ForwardButton.IsEnabled = canGoForward;
        BackMenuItem.IsEnabled = canGoBack;
        ForwardMenuItem.IsEnabled = canGoForward;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack)
        {
            Browser.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward)
        {
            Browser.GoForward();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Browser.Reload();
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        Navigate(NormalizeUrl(_serverProfile.StartupUrl));
    }

    private void BusinessButton_Click(object sender, RoutedEventArgs e)
    {
        Navigate(NormalizeUrl(_serverProfile.BusinessUrl));
    }

    private void SysAdminButton_Click(object sender, RoutedEventArgs e)
    {
        Navigate(NormalizeUrl(_serverProfile.SysAdminUrl));
    }

    private void SystemSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Navigate(BuildBusinessRouteUrl("session"));
    }

    private void SystemBridgeTestMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SendDesktopBridgeCommand(
            "shell.ping",
            new
            {
                source = "System menu",
                url = Browser.Source?.ToString() ?? AddressTextBox.Text,
                profile = _serverProfile.Name
            });
    }

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        Navigate(NormalizeUrl(AddressTextBox.Text.Trim()));
    }

    private void SystemStatusMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new SystemStatusWindow(BuildSystemStatusSnapshot())
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void ShowBrowserControlsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        BrowserToolbar.Visibility = ShowBrowserControlsMenuItem.IsChecked
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void CheckServicesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckServicesAsync();
    }

    private async void StartStackButton_Click(object sender, RoutedEventArgs e)
    {
        await StartServerAsync();
    }

    private async void StopStackButton_Click(object sender, RoutedEventArgs e)
    {
        await StopServerAsync();
    }

    private async void RestartStackButton_Click(object sender, RoutedEventArgs e)
    {
        await RestartServerAsync();
    }

    private void OpenBusinessButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(_serverProfile.BusinessUrl);
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowDiagnosticsReportAsync();
    }

    private void ConnectionDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowDiagnostics("Aiseworks connection details", BuildConnectionDetailsReport());
    }

    private async void BackupDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        await BackupDatabaseAsync();
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenBackupFolder();
    }

    private async void ShowLogsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowLogsAsync();
    }

    private async Task CheckServicesAsync()
    {
        CheckServicesButton.IsEnabled = false;
        StatusTextBlock.Text = "Checking server";
        SetHealthSummary("Checking", null);
        PostgresHealthTextBlock.Text = "Checking";
        AccountingApiHealthTextBlock.Text = "Checking";
        BusinessHealthTextBlock.Text = "Checking";
        SysAdminApiHealthTextBlock.Text = "Checking";
        SysAdminHealthTextBlock.Text = "Checking";
        ServiceStatusTextBlock.Text = IsLocalServiceProfile() ? "Checking" : "N/A";

        try
        {
            var healthTask = ProbeStackAsync();
            var serviceTask = ProbeServiceAsync();
            await Task.WhenAll(healthTask, serviceTask);

            _lastHealthResults = await healthTask;
            _lastServiceProbe = await serviceTask;
            _lastHealthCheckedAt = DateTimeOffset.Now;
            ApplyHealthResults(_lastHealthResults);
            ApplyServiceProbeResult(_lastServiceProbe);
        }
        finally
        {
            CheckServicesButton.IsEnabled = true;
        }
    }

    private async Task StartServerAsync()
    {
        if (!CanManageSelectedServer("start the server"))
        {
            return;
        }

        SetServerControlButtonsEnabled(false);
        DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: starting";

        try
        {
            var result = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("up", "-d")
                : await RunServiceControlAsync("start");

            if (result.ExitCode == 0)
            {
                DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: started";
                await CheckServicesAsync();
                return;
            }

            DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: start failed";
            ShowDiagnostics("Aiseworks server start failed", result.ToDisplayText());
        }
        finally
        {
            SetServerControlButtonsEnabled(true);
        }
    }

    private async Task StopServerAsync()
    {
        if (!CanManageSelectedServer("stop the server"))
        {
            return;
        }

        if (!ConfirmServerControl("Stop server", "Stop the selected Aiseworks server now? Current users may be disconnected."))
        {
            return;
        }

        SetServerControlButtonsEnabled(false);
        DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: stopping";

        try
        {
            var result = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("stop")
                : await RunServiceControlAsync("stop");

            if (result.ExitCode == 0)
            {
                DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: stopped";
                await CheckServicesAsync();
                return;
            }

            DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: stop failed";
            ShowDiagnostics("Aiseworks server stop failed", result.ToDisplayText());
        }
        finally
        {
            SetServerControlButtonsEnabled(true);
        }
    }

    private async Task RestartServerAsync()
    {
        if (!CanManageSelectedServer("restart the server"))
        {
            return;
        }

        if (!ConfirmServerControl("Restart server", "Restart the selected Aiseworks server now? Current users may be disconnected."))
        {
            return;
        }

        SetServerControlButtonsEnabled(false);
        DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: restarting";

        try
        {
            var result = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("restart")
                : await RestartWindowsServiceAsync();

            if (result.ExitCode == 0)
            {
                DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: restarted";
                await CheckServicesAsync();
                return;
            }

            DockerStatusTextBlock.Text = $"{GetControlSurfaceLabel()}: restart failed";
            ShowDiagnostics("Aiseworks server restart failed", result.ToDisplayText());
        }
        finally
        {
            SetServerControlButtonsEnabled(true);
        }
    }

    private async Task ShowLogsAsync()
    {
        if (!EnsureDockerTestStackProfile("read Docker test-stack logs"))
        {
            return;
        }

        SetServerControlButtonsEnabled(false);
        DockerStatusTextBlock.Text = "Docker: reading test logs";

        try
        {
            var result = await RunDockerComposeAsync(
                "logs",
                "--tail",
                "160",
                "business-web",
                "accounting-api",
                "sysadmin-web",
                "sysadmin-api",
                "postgres");

            DockerStatusTextBlock.Text = result.ExitCode == 0
                ? "Docker: logs loaded"
                : "Docker: logs failed";
            ShowDiagnostics("Aiseworks Docker logs", result.ToDisplayText());
        }
        finally
        {
            SetServerControlButtonsEnabled(true);
        }
    }

    private void SetServerControlButtonsEnabled(bool enabled)
    {
        var canManageServer = enabled && CanManageSelectedServer();
        var canUseTestStack = enabled && IsDockerTestStackProfile();

        StartStackButton.IsEnabled = canManageServer;
        StopStackButton.IsEnabled = canManageServer;
        RestartStackButton.IsEnabled = canManageServer;
        ShowLogsButton.IsEnabled = canUseTestStack;
        BackupDatabaseButton.IsEnabled = canUseTestStack;
        StartServerMenuItem.IsEnabled = canManageServer;
        StopServerMenuItem.IsEnabled = canManageServer;
        RestartServerMenuItem.IsEnabled = canManageServer;
        ShowLogsMenuItem.IsEnabled = canUseTestStack;
        BackupDatabaseMenuItem.IsEnabled = canUseTestStack;
    }

    private bool CanManageSelectedServer(string? operation = null)
    {
        if (IsDockerTestStackProfile())
        {
            return true;
        }

        if (IsLocalServiceProfile() && !string.IsNullOrWhiteSpace(_serverProfile.ServiceName))
        {
            return true;
        }

        if (operation is null)
        {
            return false;
        }

        ShowDiagnostics(
            "Aiseworks server control unavailable",
            $"""
            Aiseworks cannot {operation} for the selected profile.

            Current profile: {_serverProfile.Name}
            Profile kind: {_serverProfile.Kind}
            Service name: {FormatOptional(_serverProfile.ServiceName)}

            Use a DockerTestStack profile or a LocalService profile with a configured Windows Service name.
            """);
        return false;
    }

    private string GetServerControlTooltip(string action)
    {
        if (IsDockerTestStackProfile())
        {
            return $"{UppercaseFirst(action)} the local Docker test stack";
        }

        if (IsLocalServiceProfile() && !string.IsNullOrWhiteSpace(_serverProfile.ServiceName))
        {
            return $"{UppercaseFirst(action)} Windows Service '{_serverProfile.ServiceName}'";
        }

        return "Server start/stop is available for Docker test-stack and configured local-service profiles";
    }

    private string GetControlSurfaceLabel() =>
        IsDockerTestStackProfile() ? "Docker" :
        IsLocalServiceProfile() ? "Service" :
        "Server";

    private bool ConfirmServerControl(string title, string message)
    {
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private async Task<CommandResult> RunServiceControlAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(_serverProfile.ServiceName))
        {
            return CommandResult.Failed("No Windows Service name is configured for this profile.");
        }

        return await RunScAsync(command, _serverProfile.ServiceName);
    }

    private async Task<CommandResult> RestartWindowsServiceAsync()
    {
        var stop = await RunServiceControlAsync("stop");
        var stopped = await WaitForWindowsServiceStatusAsync("Stopped", TimeSpan.FromSeconds(20));

        if (stop.ExitCode != 0 && !stopped)
        {
            return stop;
        }

        var start = await RunServiceControlAsync("start");
        if (start.ExitCode != 0)
        {
            return new CommandResult(
                start.ExitCode,
                $"{stop.StandardOutput}{Environment.NewLine}{start.StandardOutput}",
                $"{stop.StandardError}{Environment.NewLine}{start.StandardError}");
        }

        return new CommandResult(
            0,
            $"{stop.StandardOutput}{Environment.NewLine}{start.StandardOutput}",
            $"{stop.StandardError}{Environment.NewLine}{start.StandardError}");
    }

    private async Task<bool> WaitForWindowsServiceStatusAsync(string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var probe = await ProbeServiceAsync();
            if (string.Equals(probe.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return false;
    }

    private static async Task<CommandResult> RunScAsync(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Failed(ex.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return CommandResult.Failed("Windows Service control command timed out.");
        }

        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private async Task<CommandResult> RunDockerComposeAsync(params string[] composeArguments)
    {
        var composeFile = ResolveConfiguredPath(_options.DockerComposeFile);
        var envFile = ResolveConfiguredPath(_options.DockerEnvFile);

        if (!File.Exists(composeFile))
        {
            return CommandResult.Failed($"Compose file not found: {composeFile}");
        }

        if (!File.Exists(envFile))
        {
            return CommandResult.Failed($"Compose env file not found: {envFile}");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = _repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.ArgumentList.Add("compose");
        process.StartInfo.ArgumentList.Add("--env-file");
        process.StartInfo.ArgumentList.Add(envFile);
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(_options.DockerProjectName);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(composeFile);

        foreach (var argument in composeArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Failed(ex.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private async Task<CommandResult> RunDockerComposeOutputToFileAsync(
        string outputPath,
        params string[] composeArguments)
    {
        var composeFile = ResolveConfiguredPath(_options.DockerComposeFile);
        var envFile = ResolveConfiguredPath(_options.DockerEnvFile);

        if (!File.Exists(composeFile))
        {
            return CommandResult.Failed($"Compose file not found: {composeFile}");
        }

        if (!File.Exists(envFile))
        {
            return CommandResult.Failed($"Compose env file not found: {envFile}");
        }

        FileStream output;
        try
        {
            output = File.Create(outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failed($"Unable to create backup file '{outputPath}': {ex.Message}");
        }

        await using var outputScope = output;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = _repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.ArgumentList.Add("compose");
        process.StartInfo.ArgumentList.Add("--env-file");
        process.StartInfo.ArgumentList.Add(envFile);
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(_options.DockerProjectName);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(composeFile);

        foreach (var argument in composeArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            outputScope.Close();
            TryDeleteFile(outputPath);
            return CommandResult.Failed(ex.Message);
        }

        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copyTask;

        return new CommandResult(
            process.ExitCode,
            $"Wrote backup stream to {outputPath}",
            await errorTask);
    }

    private string ResolveConfiguredPath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_repositoryRoot, path));
    }

    private void ShowDiagnostics(string title, string diagnostics)
    {
        var window = new DiagnosticsWindow(title, diagnostics)
        {
            Owner = this,
            Title = title
        };

        window.Show();
    }

    private async Task ShowDiagnosticsReportAsync()
    {
        DiagnosticsButton.IsEnabled = false;
        DockerStatusTextBlock.Text = "Server: building diagnostics";

        try
        {
            if (_lastHealthResults.Count == 0)
            {
                _lastHealthResults = await ProbeStackAsync();
                _lastHealthCheckedAt = DateTimeOffset.Now;
                ApplyHealthResults(_lastHealthResults);
            }

            var psResult = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("ps")
                : CommandResult.Failed("Docker status was not collected because the selected profile is not a Docker test stack.");
            var report = BuildDiagnosticsReport(psResult);
            DockerStatusTextBlock.Text = "Server: diagnostics ready";
            ShowDiagnostics("Aiseworks diagnostics report", report);
        }
        finally
        {
            DiagnosticsButton.IsEnabled = true;
        }
    }

    private async Task BackupDatabaseAsync()
    {
        if (!EnsureDockerTestStackProfile("create a test database backup"))
        {
            return;
        }

        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Aiseworks Backups");

        try
        {
            Directory.CreateDirectory(backupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowDiagnostics(
                "Aiseworks database backup failed",
                $"Unable to create backup directory '{backupDirectory}': {ex.Message}");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Aiseworks PostgreSQL backup",
            InitialDirectory = backupDirectory,
            FileName = $"aiseworks-postgres-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.dump",
            DefaultExt = ".dump",
            Filter = "PostgreSQL custom dump (*.dump)|*.dump|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetServerControlButtonsEnabled(false);
        DockerStatusTextBlock.Text = "Docker: checking before backup";

        try
        {
            _lastHealthResults = await ProbeStackAsync();
            _lastHealthCheckedAt = DateTimeOffset.Now;
            ApplyHealthResults(_lastHealthResults);

            var unhealthy = _lastHealthResults
                .Where(result => !result.IsHealthy)
                .Select(result => $"- {result.Name}: {result.Status} ({result.Target})");
            var unhealthyText = string.Join(Environment.NewLine, unhealthy);
            if (!string.IsNullOrWhiteSpace(unhealthyText))
            {
                DockerStatusTextBlock.Text = "Docker: backup blocked";
                ShowDiagnostics(
                    "Aiseworks database backup blocked",
                    $"""
                    Backup was not started because the local test stack is not healthy.

                    {unhealthyText}
                    """);
                return;
            }

            DockerStatusTextBlock.Text = "Docker: backing up database";
            var result = await RunDockerComposeOutputToFileAsync(
                dialog.FileName,
                "exec",
                "-T",
                "postgres",
                "sh",
                "-c",
                "pg_dump -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" --format=custom --no-owner --no-privileges");

            var file = new FileInfo(dialog.FileName);
            if (result.ExitCode == 0 && file.Exists && file.Length > 0)
            {
                var completedAt = DateTimeOffset.Now;
                _lastBackupFile = dialog.FileName;
                _backupState = new BackupState
                {
                    LastBackupFile = dialog.FileName,
                    LastBackupCompletedAt = completedAt,
                    LastBackupSizeBytes = file.Length
                };
                UpdateLastBackupText();
                SaveBackupState();
                DockerStatusTextBlock.Text = "Docker: backup completed";
                ShowDiagnostics(
                    "Aiseworks database backup completed",
                    BuildBackupReport(dialog.FileName, file.Length, completedAt, result));
                return;
            }

            if (file.Exists)
            {
                file.Delete();
            }

            DockerStatusTextBlock.Text = "Docker: backup failed";
            ShowDiagnostics("Aiseworks database backup failed", result.ToDisplayText());
        }
        finally
        {
            SetServerControlButtonsEnabled(true);
        }
    }

    private bool EnsureDockerTestStackProfile(string operation)
    {
        if (IsDockerTestStackProfile())
        {
            return true;
        }

        ShowDiagnostics(
            "Aiseworks test-stack operation blocked",
            $"""
            Aiseworks cannot {operation} while the selected profile is not a Docker test stack.

            Current profile: {_serverProfile.Name}
            Profile kind: {_serverProfile.Kind}

            This prevents local Docker actions from being mixed with a production or LAN server connection.
            """);
        return false;
    }

    private bool IsDockerTestStackProfile()
    {
        return string.Equals(
            _serverProfile.Kind,
            ServerProfileKinds.DockerTestStack,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLocalServiceProfile()
    {
        return string.Equals(
            _serverProfile.Kind,
            ServerProfileKinds.LocalService,
            StringComparison.OrdinalIgnoreCase);
    }

    private string BuildBackupReport(
        string fileName,
        long fileSize,
        DateTimeOffset completedAt,
        CommandResult result)
    {
        return $"""
            Aiseworks Test Database Backup
            Completed: {completedAt:yyyy-MM-dd HH:mm:ss zzz}

            Backup file
            Path: {fileName}
            Size: {fileSize:N0} bytes
            Format: PostgreSQL custom dump

            Source
            Docker project: {_options.DockerProjectName}
            Docker service: postgres
            Database: container POSTGRES_DB
            User: container POSTGRES_USER

            Safety boundary
            This operation is for the local Docker test stack only. It creates a
            backup file and does not restore, overwrite, migrate, or change
            PostgreSQL data.

            Command result
            {result.ToDisplayText()}
            """;
    }

    private void OpenBackupFolder()
    {
        var backupDirectory = !string.IsNullOrWhiteSpace(_lastBackupFile)
            ? Path.GetDirectoryName(_lastBackupFile)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Aiseworks Backups");

        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(backupDirectory);
            Process.Start(new ProcessStartInfo(backupDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowDiagnostics(
                "Aiseworks backup folder unavailable",
                $"Unable to open backup folder '{backupDirectory}': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string BuildDiagnosticsReport(CommandResult dockerPs)
    {
        var composeFile = ResolveConfiguredPath(_options.DockerComposeFile);
        var envFile = ResolveConfiguredPath(_options.DockerEnvFile);
        var checkedAt = _lastHealthCheckedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "not checked";
        var healthLines = _lastHealthResults.Count == 0
            ? "No health check has been run."
            : string.Join(
                Environment.NewLine,
                _lastHealthResults.Select(result =>
                    $"- {result.Name}: {result.Status} ({result.Target})"));

        return $"""
            Aiseworks Diagnostics Report
            Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}

            Health
            Last checked: {checkedAt}
            {healthLines}

            Desktop shell
            Repository root: {_repositoryRoot}
            Profile: {_serverProfile.Name}
            Profile kind: {_serverProfile.Kind}
            Profile description: {FormatOptional(_serverProfile.Description)}
            Service name: {FormatOptional(_serverProfile.ServiceName)}
            Service target: {_lastServiceProbe.Target}
            Service status: {_lastServiceProbe.Status}
            Service management: {GetServiceManagementStatus()}
            Business URL: {_serverProfile.BusinessUrl}
            SysAdmin URL: {_serverProfile.SysAdminUrl}
            Startup URL: {_serverProfile.StartupUrl}

            Health targets
            Postgres: {_serverProfile.PostgresHost}:{_serverProfile.PostgresPort}
            Accounting API: {_serverProfile.AccountingApiHealthUrl}
            Business UI: {_serverProfile.BusinessHealthUrl}
            SysAdmin API: {_serverProfile.SysAdminApiHealthUrl}
            SysAdmin UI: {_serverProfile.SysAdminHealthUrl}

            Docker test stack
            Project: {_options.DockerProjectName}
            Compose file: {composeFile}
            Env file: {envFile}
            Backup state file: {_stateFilePath}

            Docker compose ps
            {dockerPs.ToDisplayText()}
            """;
    }

    private string BuildConnectionDetailsReport()
    {
        var testStackCapability = IsDockerTestStackProfile()
            ? "Enabled"
            : "Disabled for this profile";
        var lastChecked = _lastHealthCheckedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "not checked";
        var healthLines = _lastHealthResults.Count == 0
            ? "No health check has been run for this profile."
            : string.Join(
                Environment.NewLine,
                _lastHealthResults.Select(result =>
                    $"- {result.Name}: {result.Status} ({result.Target})"));

        return $"""
            Aiseworks Connection Details

            Active profile
            Name: {_serverProfile.Name}
            Kind: {_serverProfile.Kind}
            Description: {FormatOptional(_serverProfile.Description)}
            Service name: {FormatOptional(_serverProfile.ServiceName)}
            Service target: {_lastServiceProbe.Target}
            Service status: {_lastServiceProbe.Status}
            Service management: {GetServiceManagementStatus()}

            Navigation
            Startup URL: {_serverProfile.StartupUrl}
            Business URL: {_serverProfile.BusinessUrl}
            SysAdmin URL: {_serverProfile.SysAdminUrl}

            Health targets
            Last checked: {lastChecked}
            Postgres: {_serverProfile.PostgresHost}:{_serverProfile.PostgresPort}
            Accounting API: {_serverProfile.AccountingApiHealthUrl}
            Business UI: {_serverProfile.BusinessHealthUrl}
            SysAdmin API: {_serverProfile.SysAdminApiHealthUrl}
            SysAdmin UI: {_serverProfile.SysAdminHealthUrl}

            Current health
            {healthLines}

            Desktop capabilities
            Docker test-stack start/stop/restart/logs: {testStackCapability}
            Test database backup: {testStackCapability}
            Windows service management: {GetServiceManagementStatus()}
            Diagnostics report: Enabled
            Browser launch: Enabled
            Backup folder: Enabled

            Configured profiles: {_serverProfiles.Count}
            Profile selection persistence: session only
            """;
    }

    private async Task<IReadOnlyList<HealthCheckResult>> ProbeStackAsync()
    {
        var checks = new Task<HealthCheckResult>[]
        {
            ProbeTcpAsync("Postgres", _serverProfile.PostgresHost, _serverProfile.PostgresPort),
            ProbeHttpAsync("Accounting API", _serverProfile.AccountingApiHealthUrl),
            ProbeHttpAsync("Business UI", _serverProfile.BusinessHealthUrl),
            ProbeHttpAsync("SysAdmin API", _serverProfile.SysAdminApiHealthUrl),
            ProbeHttpAsync("SysAdmin UI", _serverProfile.SysAdminHealthUrl)
        };

        return await Task.WhenAll(checks);
    }

    private void ApplyHealthResults(IReadOnlyList<HealthCheckResult> results)
    {
        PostgresHealthTextBlock.Text = FindStatus(results, "Postgres");
        AccountingApiHealthTextBlock.Text = FindStatus(results, "Accounting API");
        BusinessHealthTextBlock.Text = FindStatus(results, "Business UI");
        SysAdminApiHealthTextBlock.Text = FindStatus(results, "SysAdmin API");
        SysAdminHealthTextBlock.Text = FindStatus(results, "SysAdmin UI");

        var healthy = results.All(result => result.IsHealthy);
        StatusTextBlock.Text = healthy ? "Ready" : "Server check failed";
        SetHealthSummary(healthy ? "Online" : "Check failed", healthy);
        DockerStatusTextBlock.Text = healthy
            ? "Server: healthy"
            : "Server: check failed";
    }

    private void ApplyServiceProbeResult(ServiceProbeResult result)
    {
        _lastServiceProbe = result;
        ServiceStatusTextBlock.Text = result.Status;
        ServiceStatusTextBlock.ToolTip = $"{result.Target}: {result.Detail}";
    }

    private static string FindStatus(IReadOnlyList<HealthCheckResult> results, string name)
    {
        return results.FirstOrDefault(result => result.Name == name)?.Status ?? "Unknown";
    }

    private static HealthCheckResult HealthResultOrDefault(
        IReadOnlyList<HealthCheckResult> results,
        string name,
        string fallbackTarget)
    {
        return results.FirstOrDefault(result => result.Name == name)
            ?? new HealthCheckResult(name, fallbackTarget, "Unknown", false);
    }

    private void SetHealthSummary(string value, bool? healthy)
    {
        HealthSummaryTextBlock.Text = value;
        HealthSummaryTextBlock.Foreground = healthy switch
        {
            true => new SolidColorBrush(Color.FromRgb(5, 122, 85)),
            false => new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            _ => new SolidColorBrush(Color.FromRgb(107, 114, 128))
        };
    }

    private SystemStatusSnapshot BuildSystemStatusSnapshot()
    {
        var postgres = HealthResultOrDefault(
            _lastHealthResults,
            "Postgres",
            $"{_serverProfile.PostgresHost}:{_serverProfile.PostgresPort}");
        var accounting = HealthResultOrDefault(
            _lastHealthResults,
            "Accounting API",
            _serverProfile.AccountingApiHealthUrl);
        var business = HealthResultOrDefault(
            _lastHealthResults,
            "Business UI",
            _serverProfile.BusinessHealthUrl);
        var sysAdminApi = HealthResultOrDefault(
            _lastHealthResults,
            "SysAdmin API",
            _serverProfile.SysAdminApiHealthUrl);
        var sysAdmin = HealthResultOrDefault(
            _lastHealthResults,
            "SysAdmin UI",
            _serverProfile.SysAdminHealthUrl);

        var healthSummary = _lastHealthResults.Count == 0
            ? "Unknown"
            : _lastHealthResults.All(result => result.IsHealthy)
                ? "Online"
                : "Check failed";

        return new SystemStatusSnapshot(
            ProfileName: _serverProfile.Name,
            ProfileDescription: FormatOptional(_serverProfile.Description),
            Mode: GetProfileModeText().Replace("Mode: ", "", StringComparison.Ordinal),
            ServiceStatus: _lastServiceProbe.Status,
            ServiceTarget: _lastServiceProbe.Target,
            ServiceDetail: _lastServiceProbe.Detail,
            PostgresStatus: postgres.Status,
            PostgresTarget: postgres.Target,
            AccountingStatus: accounting.Status,
            AccountingTarget: accounting.Target,
            BusinessStatus: business.Status,
            BusinessTarget: business.Target,
            SysAdminApiStatus: sysAdminApi.Status,
            SysAdminApiTarget: sysAdminApi.Target,
            SysAdminStatus: sysAdmin.Status,
            SysAdminTarget: sysAdmin.Target,
            LastChecked: _lastHealthCheckedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "Never",
            HealthSummary: healthSummary);
    }

    private async Task<ServiceProbeResult> ProbeServiceAsync()
    {
        if (!IsLocalServiceProfile())
        {
            return ServiceProbeResult.NotApplicable();
        }

        if (string.IsNullOrWhiteSpace(_serverProfile.ServiceName))
        {
            return new ServiceProbeResult(
                "Not configured",
                "LocalService",
                "No Windows Service name is configured for this profile.");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("query");
        process.StartInfo.ArgumentList.Add(_serverProfile.ServiceName);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ServiceProbeResult(
                "Unavailable",
                _serverProfile.ServiceName,
                $"Unable to query Windows Service status: {ex.Message}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new ServiceProbeResult(
                "Timeout",
                _serverProfile.ServiceName,
                "Windows Service status query timed out.");
        }

        var output = await outputTask;
        var error = await errorTask;
        var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();

        if (process.ExitCode != 0)
        {
            var status = detail.Contains("1060", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                ? "Not installed"
                : "Query failed";
            return new ServiceProbeResult(status, _serverProfile.ServiceName, detail);
        }

        var stateLine = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase));

        if (stateLine is null)
        {
            return new ServiceProbeResult("Unknown", _serverProfile.ServiceName, output.Trim());
        }

        if (stateLine.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceProbeResult("Running", _serverProfile.ServiceName, stateLine);
        }

        if (stateLine.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceProbeResult("Stopped", _serverProfile.ServiceName, stateLine);
        }

        return new ServiceProbeResult("Installed", _serverProfile.ServiceName, stateLine);
    }

    private string GetProfileModeText()
    {
        return _serverProfile.Kind switch
        {
            ServerProfileKinds.DockerTestStack => "Mode: Docker test stack",
            ServerProfileKinds.LocalService => "Mode: Local service",
            ServerProfileKinds.LanServer => "Mode: LAN server",
            _ => $"Mode: {_serverProfile.Kind}"
        };
    }

    private string GetServiceManagementStatus()
    {
        return _serverProfile.Kind switch
        {
            ServerProfileKinds.DockerTestStack => "Docker Compose test-stack start, stop, and restart are available for this profile.",
            ServerProfileKinds.LocalService => string.IsNullOrWhiteSpace(_serverProfile.ServiceName)
                ? "Windows Service control requires a configured ServiceName."
                : $"Windows Service start, stop, and restart target '{_serverProfile.ServiceName}'.",
            ServerProfileKinds.LanServer => "Remote server service management is not available from this desktop shell.",
            _ => "No service management capability is defined for this profile kind."
        };
    }

    private string GetServiceTargetText()
    {
        return _serverProfile.Kind switch
        {
            ServerProfileKinds.LocalService when !string.IsNullOrWhiteSpace(_serverProfile.ServiceName)
                => _serverProfile.ServiceName,
            ServerProfileKinds.LocalService => "LocalService",
            ServerProfileKinds.DockerTestStack => "Docker test stack",
            ServerProfileKinds.LanServer => "LAN server",
            _ => _serverProfile.Kind
        };
    }

    private static string FormatOptional(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string UppercaseFirst(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static async Task<HealthCheckResult> ProbeHttpAsync(string name, string url)
    {
        try
        {
            using var response = await HealthClient.GetAsync(NormalizeUrl(url));
            var status = response.IsSuccessStatusCode
                ? "Online"
                : $"HTTP {(int)response.StatusCode}";
            return new HealthCheckResult(name, NormalizeUrl(url), status, response.IsSuccessStatusCode);
        }
        catch (HttpRequestException)
        {
            return new HealthCheckResult(name, NormalizeUrl(url), "Offline", false);
        }
        catch (TaskCanceledException)
        {
            return new HealthCheckResult(name, NormalizeUrl(url), "Timeout", false);
        }
    }

    private static async Task<HealthCheckResult> ProbeTcpAsync(string name, string host, int port)
    {
        var target = $"{host}:{port}";

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(3));
            return new HealthCheckResult(name, target, "Online", true);
        }
        catch (SocketException)
        {
            return new HealthCheckResult(name, target, "Offline", false);
        }
        catch (TimeoutException)
        {
            return new HealthCheckResult(name, target, "Timeout", false);
        }
        catch (OperationCanceledException)
        {
            return new HealthCheckResult(name, target, "Timeout", false);
        }
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(NormalizeUrl(url))
        {
            UseShellExecute = true
        });
    }

    private void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        Navigate(NormalizeUrl(AddressTextBox.Text.Trim()));
    }
}

internal sealed record HealthCheckResult(string Name, string Target, string Status, bool IsHealthy);

internal sealed record ServiceProbeResult(string Status, string Target, string Detail)
{
    public static ServiceProbeResult NotApplicable()
    {
        return new ServiceProbeResult(
            "N/A",
            "Current profile",
            "Windows Service status is not applicable to the selected profile.");
    }

    public static ServiceProbeResult NotChecked(string target)
    {
        return new ServiceProbeResult(
            "Not checked",
            target,
            "Windows Service status has not been checked in this desktop session.");
    }
}

internal sealed class BackupState
{
    public string? LastBackupFile { get; init; }

    public DateTimeOffset? LastBackupCompletedAt { get; init; }

    public long? LastBackupSizeBytes { get; init; }
}

internal sealed record DesktopBridgeMessage
{
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("sentAt")]
    public DateTimeOffset? SentAt { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

internal sealed record DesktopBridgeOutgoingMessage(
    string Channel,
    string Direction,
    string Type,
    string Id,
    DateTimeOffset SentAt,
    object? Payload = null,
    string? ReplyTo = null);

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public static CommandResult Failed(string message)
    {
        return new CommandResult(-1, "", message);
    }

    public string ToDisplayText()
    {
        return $"""
            Exit code: {ExitCode}

            Standard output:
            {StandardOutput}

            Standard error:
            {StandardError}
            """;
    }
}
