param(
    [int]$Port = 5088
)

$ErrorActionPreference = "Stop"

$backendRoot = $PSScriptRoot
$stateDir = Join-Path $env:TEMP "citus-accounting-api"
$pidFile = Join-Path $stateDir "accounting-api-$Port.pid"
$stopped = $false

function Stop-ApiProcess {
    param(
        [int]$ProcessId
    )

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $false
    }

    Stop-Process -Id $ProcessId -Force
    Write-Host "Stopped accounting API process $ProcessId."
    return $true
}

if (Test-Path $pidFile) {
    $rawPid = (Get-Content -Path $pidFile -Raw).Trim()
    if (-not [string]::IsNullOrWhiteSpace($rawPid)) {
        $stopped = Stop-ApiProcess -ProcessId ([int]$rawPid)
    }

    Remove-Item -Path $pidFile -ErrorAction SilentlyContinue
}

if (-not $stopped) {
    $matchingProcesses = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*Citus.Accounting.Api*"
    }

    foreach ($process in $matchingProcesses) {
        if (Stop-ApiProcess -ProcessId ([int]$process.ProcessId)) {
            $stopped = $true
        }
    }
}

if (-not $stopped) {
    Write-Host "Accounting API is not running."
}
