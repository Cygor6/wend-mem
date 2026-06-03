#Requires -Version 7.4
<#
.SYNOPSIS
    Starts wendmem in HTTP mode with automatic restart.

.DESCRIPTION
    Two-step health check during STARTUP:
      1. Get-NetTCPConnection - fast TCP listener check.
      2. Simple HTTP GET / - verifies that ASP.NET Core responds.

    During OPERATION: no polling at all. proc.WaitForExit() blocks until the process exits.
    No repeated HTTP calls -> no log spam in wendmem.

.PARAMETER ExePath          Path to Wendmem.exe. Auto-resolved from PATH if omitted.
.PARAMETER DbPath           Path to palace.duckdb, or set WENDMEM_DB in the environment.
.PARAMETER Port             Port to listen on. Default 5133.
.PARAMETER MaxCrashes       Max rapid crashes in a row before the script gives up. 0=unlimited.
.PARAMETER MinUptimeSeconds Uptime shorter than this counts as a rapid crash. Default 15.
.PARAMETER StartupTimeout   Seconds to wait for TCP listener after process start. Default 45.
.PARAMETER LogDir           Folder for log files. Default: %TEMP%\wendmem-logs
#>
[CmdletBinding()]
param(
    [string] $ExePath = '',
    [string] $DbPath  = '',

    [ValidateRange(1, 65535)]
    [int] $Port = 5133,

    [ValidateRange(0, 2147483647)]
    [int] $MaxCrashes = 8,

    [ValidateRange(0, 2147483647)]
    [int] $MinUptimeSeconds = 15,

    [ValidateRange(1, 2147483647)]
    [int] $StartupTimeout = 45,

    [string] $LogDir = "$env:TEMP\wendmem-logs"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$BackoffBase = 2
$BackoffMax  = 60

# --- Logging ---------------------------------------------------------------

$null = New-Item -ItemType Directory -Path $LogDir -Force
$LogFile = Join-Path $LogDir "wendmem-$(Get-Date -Format 'yyyy-MM-dd').log"

function Write-Log
{
    param(
        [string] $Message,
        [ValidateSet('INFO', 'OK', 'WARN', 'ERROR')]
        [string] $Level = 'INFO'
    )

    $line = "[$(Get-Date -Format 'HH:mm:ss')] [$Level] $Message"

    Add-Content -Path $LogFile -Value $line -Encoding UTF8

    Write-Host $line -ForegroundColor $(switch ($Level)
        {
            'OK'
            { 'Green'
            }
            'WARN'
            { 'Yellow'
            }
            'ERROR'
            { 'Red'
            }
            default
            { 'Gray'
            }
        })
}

# --- Validation -------------------------------------------------------------

if (-not $IsWindows)
{
    Write-Log "This script requires Windows because it uses Get-NetTCPConnection." 'ERROR'
    exit 1
}

if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue))
{
    Write-Log "Get-NetTCPConnection not found. The NetTCPIP module may not be available." 'ERROR'
    exit 1
}

# --- Resolve ExePath if not specified -------------------------------------
if (-not $ExePath)
{
    $found = Get-Command wendmem -ErrorAction SilentlyContinue
    if ($found)
    {
        $ExePath = $found.Source
    } else
    {
        Write-Log "ExePath not specified and wendmem not on PATH. Pass -ExePath <path>." 'ERROR'
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf))
{
    Write-Log "Wendmem.exe not found: $ExePath" 'ERROR'
    exit 1
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$ExeDir  = Split-Path -Path $ExePath

if (-not $DbPath)
{
    $DbPath = $env:WENDMEM_DB ?? ''
}

if (-not $DbPath)
{
    $candidate = Join-Path $ExeDir 'palace.duckdb'
    if (Test-Path -LiteralPath $candidate -PathType Leaf)
    {
        $DbPath = $candidate
    }
}

Write-Log '------------------------------------------------------'
Write-Log "Exe  : $ExePath"
Write-Log "DB   : $(if ($DbPath) { $DbPath } else { '(WENDMEM_DB or wendmem default)' })"
Write-Log "Port : $Port"
Write-Log "Log  : $LogFile"
Write-Log '------------------------------------------------------'

# --- Health checks ----------------------------------------------------------

function Test-PortListening
{
    param(
        [int] $OwnerProcessId = 0
    )

    $conn = @(Get-NetTCPConnection `
            -LocalPort $Port `
            -State Listen `
            -ErrorAction SilentlyContinue)

    if ($OwnerProcessId -gt 0)
    {
        $conn = @($conn | Where-Object { $_.OwningProcess -eq $OwnerProcessId })
    }

    return $conn.Count -gt 0
}

function Test-HttpResponding
{
    try
    {
        $null = Invoke-WebRequest `
            -Uri "http://localhost:$Port/health" `
            -Method GET `
            -NoProxy `
            -SkipHttpErrorCheck `
            -ConnectionTimeoutSeconds 4 `
            -OperationTimeoutSeconds 4 `
            -ErrorAction Stop

        return $true
    } catch
    {
        return $false
    }
}

function Get-WendmemProcess
{
    $conn = @(Get-NetTCPConnection `
            -LocalPort $Port `
            -State Listen `
            -ErrorAction SilentlyContinue)

    if ($conn.Count -eq 0)
    {
        return $null
    }

    $ownerPid = $conn | Select-Object -First 1 -ExpandProperty OwningProcess

    try
    {
        $proc = Get-Process -Id $ownerPid -ErrorAction Stop

        if ($proc.Path -eq $ExePath)
        {
            return $proc
        }

        Write-Log "Port $Port is owned by another process: '$($proc.ProcessName)' (PID $ownerPid)" 'WARN'
        return $null
    } catch
    {
        return $null
    }
}

# --- Start process ----------------------------------------------------------

function Start-WendmemProcess
{
    $psi                  = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName         = $ExePath
    $psi.WorkingDirectory = $ExeDir
    $psi.UseShellExecute  = $false
    $psi.CreateNoWindow   = $false

    $psi.ArgumentList.Add('serve')

    if ($DbPath)
    {
        $psi.EnvironmentVariables['WENDMEM_DB'] = $DbPath
    }

    $psi.EnvironmentVariables['Palace__HttpPort'] = "$Port"

    return [System.Diagnostics.Process]::Start($psi)
}

# --- Wait for TCP listener -------------------------------------------------

function Wait-UntilListening
{
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process] $Process,

        [int] $TimeoutSeconds = 45
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $dots     = 0

    while ([datetime]::UtcNow -lt $deadline)
    {
        if ($Process.HasExited)
        {
            return $false
        }

        if (Test-PortListening -OwnerProcessId $Process.Id)
        {
            return $true
        }

        $dots++

        if ($dots % 10 -eq 0)
        {
            $remaining = [int]($deadline - [datetime]::UtcNow).TotalSeconds
            Write-Log "  ... waiting for TCP listener (${remaining}s remaining)"
        }

        Start-Sleep -Milliseconds 300
    }

    return $false
}

# --- Backoff with jitter ----------------------------------------------------

function Get-BackoffSeconds
{
    param(
        [int] $CrashCount
    )

    $exp    = [math]::Min($BackoffBase * [math]::Pow(2, $CrashCount - 1), $BackoffMax)
    $jitter = Get-Random -Minimum 0 -Maximum 3

    return [int]($exp + $jitter)
}

# --- Check if already running -----------------------------------------------

Write-Log "Checking port $Port ..."

if (Test-PortListening)
{
    $existing = Get-WendmemProcess

    if ($existing)
    {
        Write-Log "Wendmem is already running (PID $($existing.Id)) - nothing to do." 'OK'
        exit 0
    }

    Write-Log "Port $Port is occupied by another process. Aborting." 'ERROR'
    exit 1
}

# --- Cleanup on exit --------------------------------------------------------

$script:ActiveProcess = $null

Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    if ($null -ne $script:ActiveProcess -and -not $script:ActiveProcess.HasExited)
    {
        $script:ActiveProcess | Stop-Process -Force
    }
} | Out-Null

# --- Watchdog loop ----------------------------------------------------------

Write-Log "Starting watchdog (max rapid crashes: $(if ($MaxCrashes -eq 0) { 'unlimited' } else { $MaxCrashes })) ..."
$consecutiveCrashes = 0

try
{
    while ($true)
    {
        $startTime     = [datetime]::UtcNow
        $startupFailed = $false

        Write-Log '--- Starting process ---------------------------------'

        $proc = Start-WendmemProcess
        $script:ActiveProcess = $proc

        Write-Log "Started (PID $($proc.Id)). Waiting for TCP port $Port ..."

        if (-not (Wait-UntilListening -Process $proc -TimeoutSeconds $StartupTimeout))
        {
            $startupFailed = $true

            Write-Log "Port did not open within ${StartupTimeout}s, or process died during startup." 'ERROR'

            if (-not $proc.HasExited)
            {
                $proc | Stop-Process -Force
                $null = $proc.WaitForExit(5000)
            }
        } else
        {
            Write-Log "TCP ready. Verifying HTTP ..."

            $httpOk = $false

            for ($i = 0; $i -lt 10; $i++)
            {
                if (Test-HttpResponding)
                {
                    $httpOk = $true
                    break
                }

                if ($proc.HasExited)
                {
                    $startupFailed = $true
                    break
                }

                Start-Sleep -Milliseconds 500
            }

            if ($httpOk)
            {
                Write-Log "Wendmem ready at http://localhost:$Port" 'OK'
            } elseif ($proc.HasExited)
            {
                Write-Log "Process died before HTTP could respond." 'ERROR'
            } else
            {
                Write-Log "HTTP never responded — continuing since TCP is up." 'WARN'
            }

            if (-not $proc.HasExited)
            {
                # Running: no more polling — waiting silently until process exits.
                $proc.WaitForExit()
            }
        }

        $uptimeSec = [int]([datetime]::UtcNow - $startTime).TotalSeconds
        $exitCode  = if ($proc.HasExited)
        { $proc.ExitCode
        } else
        { -1
        }

        Write-Log "Process exited (exit=$exitCode, uptime=${uptimeSec}s)." 'WARN'
        $script:ActiveProcess = $null

        if ($startupFailed -or $uptimeSec -lt $MinUptimeSeconds)
        {
            $consecutiveCrashes++

            Write-Log "Rapid crash #$consecutiveCrashes (< ${MinUptimeSeconds}s uptime or failed startup)." 'WARN'

            if ($MaxCrashes -gt 0 -and $consecutiveCrashes -ge $MaxCrashes)
            {
                Write-Log "$consecutiveCrashes rapid crashes in a row — giving up." 'ERROR'
                exit 1
            }
        } else
        {
            $consecutiveCrashes = 0
        }

        $delay = Get-BackoffSeconds -CrashCount ([math]::Max($consecutiveCrashes, 1))

        Write-Log "Restarting in ${delay}s ... (Ctrl+C to abort)"
        Start-Sleep -Seconds $delay
    }
} catch [System.Management.Automation.PipelineStoppedException]
{
    Write-Log "Interrupted by user (Ctrl+C)." 'WARN'
} catch
{
    Write-Log "Unexpected error: $($_.Exception.Message)" 'ERROR'
    exit 1
} finally
{
    if ($null -ne $script:ActiveProcess -and -not $script:ActiveProcess.HasExited)
    {
        $script:ActiveProcess | Stop-Process -Force
        $null = $script:ActiveProcess.WaitForExit(5000)
        Write-Log "Wendmem stopped."
    }

    Write-Log "Watchdog finished."
}
