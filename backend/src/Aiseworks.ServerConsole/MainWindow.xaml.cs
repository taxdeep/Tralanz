using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Aiseworks.ServerConsole;

public partial class MainWindow : Window
{
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly ShellOptions _options;
    private readonly IReadOnlyList<ServerProfileOptions> _serverProfiles;
    private readonly string _repositoryRoot;
    private readonly string _stateFilePath;
    private ServerProfileOptions _serverProfile;
    private BackupState _backupState = new();
    private bool _isEditingDatabase;

    public MainWindow()
    {
        InitializeComponent();
        _options = LoadOptions();
        _serverProfiles = ResolveServerProfiles(_options);
        _serverProfile = ResolveServerProfile(_options, _serverProfiles);
        _repositoryRoot = ResolveRepositoryRoot(_options);
        _stateFilePath = ResolveStateFilePath();
        LoadBackupState();
        ApplyProfile();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog("Server console ready.");
        await CheckAllAsync();
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
                Kind = ServerProfileKinds.DockerTestStack,
                Description = "Compatibility profile built from legacy flat settings.",
                AccountingApiHealthUrl = options.AccountingApiHealthUrl,
                BusinessHealthUrl = options.BusinessHealthUrl,
                SysAdminApiHealthUrl = options.SysAdminApiHealthUrl,
                SysAdminHealthUrl = options.SysAdminHealthUrl,
                PostgresHost = options.PostgresHost,
                PostgresPort = options.PostgresPort
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

    private void ApplyProfile()
    {
        ProfileModeTextBlock.Text = _serverProfile.Kind switch
        {
            ServerProfileKinds.DockerTestStack => "Mode: Docker test stack (server + database)",
            ServerProfileKinds.LocalService => "Mode: Local Windows Service",
            ServerProfileKinds.LanServer => "Mode: LAN server",
            _ => $"Mode: {_serverProfile.Kind}"
        };
        RuntimeTargetTextBlock.Text = _serverProfile.Kind switch
        {
            ServerProfileKinds.DockerTestStack => "Docker test stack: server + database",
            ServerProfileKinds.LocalService => "Local Windows Service",
            ServerProfileKinds.LanServer => "LAN server",
            _ => _serverProfile.Name
        };
        ServiceTargetTextBlock.Text = $"Target: {GetServiceTargetText()}";
        PostgresTargetTextBlock.Text = $"{_serverProfile.PostgresHost}:{_serverProfile.PostgresPort}";
        ServiceStatusTextBlock.Text = "Not checked";
        PostgresStatusTextBlock.Text = "Not checked";
        AccountingStatusTextBlock.Text = "Not checked";
        SysAdminStatusTextBlock.Text = "Not checked";
        PostgresHostTextBox.Text = _serverProfile.PostgresHost;
        PostgresPortTextBox.Text = _serverProfile.PostgresPort.ToString();
        SetDatabaseEditMode(false);

        var canManage = CanManageSelectedServer();
        StartButton.IsEnabled = canManage;
        StopButton.IsEnabled = canManage;
        RestartButton.IsEnabled = canManage;

        var dockerOnly = IsDockerTestStackProfile();
        BackupButton.IsEnabled = dockerOnly;
        LogsButton.IsEnabled = dockerOnly;

        AppendLog($"Runtime target loaded: {_serverProfile.Name} ({_serverProfile.Kind}).");
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e) => await CheckAllAsync();

    private async void CheckDatabaseButton_Click(object sender, RoutedEventArgs e) => await CheckDatabaseAsync();

    private void EditDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        PostgresHostTextBox.Text = _serverProfile.PostgresHost;
        PostgresPortTextBox.Text = _serverProfile.PostgresPort.ToString();
        SetDatabaseEditMode(true);
    }

    private async void SaveDatabaseButton_Click(object sender, RoutedEventArgs e) => await SaveDatabaseConfigAsync();

    private void CancelDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        PostgresHostTextBox.Text = _serverProfile.PostgresHost;
        PostgresPortTextBox.Text = _serverProfile.PostgresPort.ToString();
        SetDatabaseEditMode(false);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartServerAsync();

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopServerAsync();

    private async void RestartButton_Click(object sender, RoutedEventArgs e) => await RestartServerAsync();

    private async void BackupButton_Click(object sender, RoutedEventArgs e) => await BackupDatabaseAsync();

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e) => OpenBackupFolder();

    private async void LogsButton_Click(object sender, RoutedEventArgs e) => await ShowLogsAsync();

    private async Task CheckAllAsync()
    {
        SetBusy(true, "Checking server");
        try
        {
            var serviceTask = ProbeServiceAsync();
            var postgresTask = ProbeTcpAsync("Postgres", _serverProfile.PostgresHost, _serverProfile.PostgresPort);
            var accountingTask = ProbeHttpAsync("Accounting API", _serverProfile.AccountingApiHealthUrl);
            var sysAdminTask = ProbeHttpAsync("SysAdmin API", _serverProfile.SysAdminApiHealthUrl);
            var businessTask = ProbeHttpAsync("Business UI", _serverProfile.BusinessHealthUrl);
            var sysAdminUiTask = ProbeHttpAsync("SysAdmin UI", _serverProfile.SysAdminHealthUrl);

            await Task.WhenAll(serviceTask, postgresTask, accountingTask, sysAdminTask, businessTask, sysAdminUiTask);

            ApplyServiceProbeResult(await serviceTask);
            ApplyDatabaseResult(await postgresTask);
            ApplyHttpResult(await accountingTask, AccountingStatusTextBlock);
            ApplyHttpResult(await sysAdminTask, SysAdminStatusTextBlock);

            AppendLog((await businessTask).ToString());
            AppendLog((await sysAdminUiTask).ToString());
            LastCheckedTextBlock.Text = $"Last checked: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async Task CheckDatabaseAsync()
    {
        SetBusy(true, "Checking database");
        try
        {
            ApplyDatabaseResult(await ProbeTcpAsync("Postgres", _serverProfile.PostgresHost, _serverProfile.PostgresPort));
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async Task SaveDatabaseConfigAsync()
    {
        var host = PostgresHostTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("PostgreSQL host is required.", "Invalid database configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PostgresPortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("PostgreSQL port must be a number between 1 and 65535.", "Invalid database configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _serverProfile.PostgresHost = host;
        _serverProfile.PostgresPort = port;
        _options.SelectedServerProfileName = _serverProfile.Name;
        _options.PostgresHost = host;
        _options.PostgresPort = port;

        try
        {
            SaveOptions();
            PostgresTargetTextBlock.Text = $"{_serverProfile.PostgresHost}:{_serverProfile.PostgresPort}";
            SetDatabaseEditMode(false);
            AppendLog($"Database configuration saved for '{_serverProfile.Name}': {host}:{port}");
            await CheckDatabaseAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Database configuration could not be saved: {ex.Message}",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task StartServerAsync()
    {
        if (!CanManageSelectedServer("start the server"))
        {
            return;
        }

        SetBusy(true, "Starting server");
        try
        {
            var result = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("up", "-d")
                : await RunServiceControlAsync("start");
            AppendLog(result.ToDisplayText());

            if (result.ExitCode == 0)
            {
                await CheckAllAsync();
            }
            else
            {
                ShowCommandFailure("Aiseworks server start failed", result);
            }
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async Task StopServerAsync()
    {
        if (!CanManageSelectedServer("stop the server")
            || !ConfirmServerControl("Stop server", "Stop the selected Aiseworks server now? Current users may be disconnected."))
        {
            return;
        }

        SetBusy(true, "Stopping server");
        try
        {
            var result = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("stop")
                : await RunServiceControlAsync("stop");
            AppendLog(result.ToDisplayText());

            if (result.ExitCode == 0)
            {
                await CheckAllAsync();
            }
            else
            {
                ShowCommandFailure("Aiseworks server stop failed", result);
            }
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async Task RestartServerAsync()
    {
        if (!CanManageSelectedServer("restart the server")
            || !ConfirmServerControl("Restart server", "Restart the selected Aiseworks server now? Current users may be disconnected."))
        {
            return;
        }

        SetBusy(true, "Restarting server");
        try
        {
            var result = IsDockerTestStackProfile()
                ? await RunDockerComposeAsync("restart")
                : await RestartWindowsServiceAsync();
            AppendLog(result.ToDisplayText());

            if (result.ExitCode == 0)
            {
                await CheckAllAsync();
            }
            else
            {
                ShowCommandFailure("Aiseworks server restart failed", result);
            }
        }
        finally
        {
            SetBusy(false, "Ready");
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
        Directory.CreateDirectory(backupDirectory);

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

        SetBusy(true, "Backing up database");
        try
        {
            var result = await RunDockerComposeOutputToFileAsync(
                dialog.FileName,
                "exec",
                "-T",
                "postgres",
                "sh",
                "-c",
                "pg_dump -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" --format=custom --no-owner --no-privileges");
            AppendLog(result.ToDisplayText());

            var file = new FileInfo(dialog.FileName);
            if (result.ExitCode == 0 && file.Exists && file.Length > 0)
            {
                _backupState = new BackupState
                {
                    LastBackupFile = dialog.FileName,
                    LastBackupCompletedAt = DateTimeOffset.Now,
                    LastBackupSizeBytes = file.Length
                };
                SaveBackupState();
                UpdateBackupStatus();
                return;
            }

            TryDeleteFile(dialog.FileName);
            ShowCommandFailure("Aiseworks database backup failed", result);
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async Task ShowLogsAsync()
    {
        if (!EnsureDockerTestStackProfile("read Docker test-stack logs"))
        {
            return;
        }

        SetBusy(true, "Reading logs");
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
            AppendLog(result.ToDisplayText());
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private static string ResolveStateFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aiseworks",
            "ServerConsole");

        return Path.Combine(directory, "backup-state.json");
    }

    private static string ResolveRepositoryRoot(ShellOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RepositoryRoot) && Directory.Exists(options.RepositoryRoot))
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
            UpdateBackupStatus();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppendLog($"Backup state could not be loaded: {ex.Message}");
        }
    }

    private void SaveBackupState()
    {
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_backupState, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_stateFilePath, json);
    }

    private void SaveOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = JsonSerializer.Serialize(_options, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void UpdateBackupStatus()
    {
        if (string.IsNullOrWhiteSpace(_backupState.LastBackupFile)
            || !_backupState.LastBackupCompletedAt.HasValue
            || !_backupState.LastBackupSizeBytes.HasValue)
        {
            BackupStatusTextBlock.Text = "Backup: none";
            return;
        }

        BackupStatusTextBlock.Text =
            $"Backup: {_backupState.LastBackupSizeBytes.Value:N0} bytes at {_backupState.LastBackupCompletedAt.Value:yyyy-MM-dd HH:mm}";
    }

    private void OpenBackupFolder()
    {
        var backupDirectory = !string.IsNullOrWhiteSpace(_backupState.LastBackupFile)
            ? Path.GetDirectoryName(_backupState.LastBackupFile)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aiseworks Backups");

        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            return;
        }

        Directory.CreateDirectory(backupDirectory);
        Process.Start(new ProcessStartInfo(backupDirectory) { UseShellExecute = true });
    }

    private void SetBusy(bool isBusy, string status)
    {
        FooterStatusTextBlock.Text = status;
        StartButton.IsEnabled = !isBusy && CanManageSelectedServer();
        StopButton.IsEnabled = !isBusy && CanManageSelectedServer();
        RestartButton.IsEnabled = !isBusy && CanManageSelectedServer();
        CheckButton.IsEnabled = !isBusy;
        CheckDatabaseButton.IsEnabled = !isBusy && !_isEditingDatabase;
        EditDatabaseButton.IsEnabled = !isBusy;
        BackupButton.IsEnabled = !isBusy && IsDockerTestStackProfile();
        LogsButton.IsEnabled = !isBusy && IsDockerTestStackProfile();
    }

    private void SetDatabaseEditMode(bool isEditing)
    {
        _isEditingDatabase = isEditing;
        DatabaseEditPanel.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        CheckDatabaseButton.IsEnabled = !isEditing;
        EditDatabaseButton.IsEnabled = !isEditing;
    }

    private void ApplyServiceProbeResult(ServiceProbeResult result)
    {
        ServiceStatusTextBlock.Text = result.Status;
        ServiceStatusTextBlock.ToolTip = $"{result.Target}: {result.Detail}";
        AppendLog($"Service: {result.Status} ({result.Target})");
    }

    private void ApplyDatabaseResult(HealthCheckResult result)
    {
        PostgresStatusTextBlock.Text = result.Status;
        AppendLog($"{result.Name}: {result.Status} ({result.Target})");
    }

    private void ApplyHttpResult(HealthCheckResult result, System.Windows.Controls.TextBlock target)
    {
        target.Text = $"{result.Status} ({result.Target})";
        AppendLog($"{result.Name}: {result.Status} ({result.Target})");
    }

    private async Task<ServiceProbeResult> ProbeServiceAsync()
    {
        if (IsDockerTestStackProfile())
        {
            return new ServiceProbeResult("Docker profile", "Docker Compose", "Use docker compose ps/logs for container detail.");
        }

        if (!IsLocalServiceProfile())
        {
            return ServiceProbeResult.NotApplicable();
        }

        if (string.IsNullOrWhiteSpace(_serverProfile.ServiceName))
        {
            return new ServiceProbeResult("Not configured", "LocalService", "No Windows Service name is configured.");
        }

        var result = await RunScAsync("query", _serverProfile.ServiceName);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            var status = detail.Contains("1060", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                ? "Not installed"
                : "Query failed";
            return new ServiceProbeResult(status, _serverProfile.ServiceName, detail.Trim());
        }

        var stateLine = result.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase));

        if (stateLine is null)
        {
            return new ServiceProbeResult("Unknown", _serverProfile.ServiceName, result.StandardOutput.Trim());
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

    private static async Task<HealthCheckResult> ProbeHttpAsync(string name, string url)
    {
        var target = NormalizeUrl(url);
        try
        {
            using var response = await HealthClient.GetAsync(target);
            var status = response.IsSuccessStatusCode ? "Online" : $"HTTP {(int)response.StatusCode}";
            return new HealthCheckResult(name, target, status, response.IsSuccessStatusCode);
        }
        catch (HttpRequestException)
        {
            return new HealthCheckResult(name, target, "Offline", false);
        }
        catch (TaskCanceledException)
        {
            return new HealthCheckResult(name, target, "Timeout", false);
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

        return await RunServiceControlAsync("start");
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
        return await RunProcessAsync("sc.exe", Directory.GetCurrentDirectory(), arguments);
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

        var arguments = new List<string>
        {
            "compose",
            "--env-file",
            envFile,
            "-p",
            _options.DockerProjectName,
            "-f",
            composeFile
        };
        arguments.AddRange(composeArguments);

        return await RunProcessAsync("docker", _repositoryRoot, arguments);
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

        await using var output = File.Create(outputPath);
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
            TryDeleteFile(outputPath);
            return CommandResult.Failed(ex.Message);
        }

        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copyTask;

        return new CommandResult(process.ExitCode, $"Wrote backup stream to {outputPath}", await errorTask);
    }

    private static async Task<CommandResult> RunProcessAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
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
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
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

            return CommandResult.Failed($"{fileName} command timed out.");
        }

        return new CommandResult(process.ExitCode, await outputTask, await errorTask);
    }

    private string ResolveConfiguredPath(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_repositoryRoot, path));

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

        if (operation is not null)
        {
            MessageBox.Show(
                $"Aiseworks cannot {operation} for this profile. Use a DockerTestStack profile or a LocalService profile with ServiceName configured.",
                "Server control unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        return false;
    }

    private bool EnsureDockerTestStackProfile(string operation)
    {
        if (IsDockerTestStackProfile())
        {
            return true;
        }

        MessageBox.Show(
            $"Aiseworks cannot {operation} while the selected profile is not a Docker test stack.",
            "Operation blocked",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private bool ConfirmServerControl(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowCommandFailure(string title, CommandResult result)
    {
        MessageBox.Show(result.ToDisplayText(), title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool IsDockerTestStackProfile() =>
        string.Equals(_serverProfile.Kind, ServerProfileKinds.DockerTestStack, StringComparison.OrdinalIgnoreCase);

    private bool IsLocalServiceProfile() =>
        string.Equals(_serverProfile.Kind, ServerProfileKinds.LocalService, StringComparison.OrdinalIgnoreCase);

    private string GetServiceTargetText() =>
        IsDockerTestStackProfile() ? "Docker Compose test stack" :
        IsLocalServiceProfile() && !string.IsNullOrWhiteSpace(_serverProfile.ServiceName) ? _serverProfile.ServiceName :
        _serverProfile.Kind;

    private static string NormalizeUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"http://{url}";

    private void AppendLog(string text)
    {
        LogTextBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
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
}
