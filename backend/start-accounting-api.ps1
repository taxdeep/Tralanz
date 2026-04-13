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
$stdoutLog = Join-Path $stateDir "accounting-api-$Port.stdout.log"
$stderrLog = Join-Path $stateDir "accounting-api-$Port.stderr.log"
$healthUrl = "http://127.0.0.1:$Port/health"
$rootUrl = "http://127.0.0.1:$Port/"

if (-not (Test-Path $projectPath)) {
    throw "Could not find project file at $projectPath."
}

if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
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

$existingProcess = Get-RunningProcessFromPidFile
if ($null -ne $existingProcess) {
    Write-Host "Accounting API is already running at http://127.0.0.1:$Port (PID $($existingProcess.Id))."
    Write-Host "Health: $healthUrl"
    exit 0
}

Remove-Item -Path $stdoutLog, $stderrLog -ErrorAction SilentlyContinue

$arguments = @(
    "run",
    "--project",
    $projectPath
)

if ($NoBuild) {
    $arguments += "--no-build"
}

if ($Foreground) {
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    & $dotnet @arguments
    exit $LASTEXITCODE
}

$process = Start-Process `
    -FilePath $dotnet `
    -ArgumentList $arguments `
    -WorkingDirectory $backendRoot `
    -PassThru `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog

$process.Id | Set-Content -Path $pidFile -NoNewline

$healthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    Start-Sleep -Milliseconds 500

    if ($process.HasExited) {
        break
    }

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

    Remove-Item -Path $pidFile -ErrorAction SilentlyContinue

    Write-Host "Accounting API failed to start."
    if (Test-Path $stdoutLog) {
        Write-Host ""
        Write-Host "STDOUT:"
        Get-Content -Path $stdoutLog
    }

    if (Test-Path $stderrLog) {
        Write-Host ""
        Write-Host "STDERR:"
        Get-Content -Path $stderrLog
    }

    exit 1
}

Write-Host "Accounting API started."
Write-Host "Root:   $rootUrl"
Write-Host "Health: $healthUrl"
Write-Host "PID:    $($process.Id)"
Write-Host "Logs:   $stdoutLog"
Write-Host "Stop:   & '$backendRoot\\stop-accounting-api.ps1' -Port $Port"
