param(
    [int]$Port = 5088,
    [switch]$NoBuild,
    [switch]$Foreground
)

$ErrorActionPreference = "Stop"

$backendRoot = $PSScriptRoot
$projectPath = Join-Path $backendRoot "src/Citus.Accounting.Api/Citus.Accounting.Api.csproj"
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$stateDir = Join-Path $env:TEMP "citus-accounting-api"
$pidFile = Join-Path $stateDir "accounting-api-$Port.pid"
$healthUrl = "http://127.0.0.1:$Port/health"
$rootUrl = "http://127.0.0.1:$Port/"

if (-not (Test-Path $projectPath)) {
    throw "Could not find project file at $projectPath."
}

if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$powershellHost = Join-Path $PSHOME "powershell.exe"
if (-not (Test-Path $powershellHost)) {
    $powershellHost = "powershell.exe"
}

New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

function Get-RunningProcessFromPidFile {
    if (-not (Test-Path $pidFile)) {
        return $null
    }

    $rawPid = (Get-Content -Path $pidFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($rawPid)) {
        Remove-Item -Path $pidFile -ErrorAction SilentlyContinue
        return $null
    }

    $process = Get-Process -Id ([int]$rawPid) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        Remove-Item -Path $pidFile -ErrorAction SilentlyContinue
        return $null
    }

    return $process
}

function Get-AccountingApiProcesses {
    Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*Citus.Accounting.Api*"
    }
}

$existingProcess = Get-RunningProcessFromPidFile
if ($null -ne $existingProcess) {
    Write-Host "Accounting API is already running at http://127.0.0.1:$Port (PID $($existingProcess.Id))."
    Write-Host "Health: $healthUrl"
    return
}

$arguments = @(
    "run",
    "--project",
    "`"$projectPath`""
)

if ($NoBuild) {
    $arguments += "--no-build"
}

$arguments += @(
    "--",
    "--urls",
    "http://127.0.0.1:$Port"
)

if ($Foreground) {
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Accounting API exited with code $LASTEXITCODE."
    }

    return
}

$argumentLine = [string]::Join(" ", $arguments)

$childScript = @"
Set-Location '$backendRoot'
& '$dotnet' $argumentLine
"@

$encodedChildScript = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($childScript))

$process = Start-Process `
    -FilePath $powershellHost `
    -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedChildScript" `
    -WorkingDirectory $backendRoot `
    -PassThru `
    -WindowStyle Hidden

$healthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    Start-Sleep -Milliseconds 500

    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2
        if ($health.status -eq "ok") {
            $healthy = $true
            break
        }
    }
    catch {
    }
}

if (-not $healthy) {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    foreach ($apiProcess in Get-AccountingApiProcesses) {
        if ($apiProcess.ProcessId -ne $process.Id) {
            Stop-Process -Id $apiProcess.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }

    Remove-Item -Path $pidFile -ErrorAction SilentlyContinue

    Write-Host "Accounting API failed to start."
    Write-Host "The process did not become healthy at $healthUrl."

    throw "Accounting API failed to start."
}

$resolvedProcess =
    Get-AccountingApiProcesses |
    Sort-Object ProcessId -Descending |
    Select-Object -First 1

if ($null -ne $resolvedProcess) {
    $resolvedProcess.ProcessId | Set-Content -Path $pidFile -NoNewline
}
else {
    $process.Id | Set-Content -Path $pidFile -NoNewline
}

Write-Host "Accounting API started."
Write-Host "Root:   $rootUrl"
Write-Host "Health: $healthUrl"
Write-Host "PID:    $(if ($null -ne $resolvedProcess) { $resolvedProcess.ProcessId } else { $process.Id })"
Write-Host "Stop:   & '$backendRoot\\stop-accounting-api.ps1' -Port $Port"
