#Requires -Version 7.5
# Dot-source this file to get Resolve-WendmemExe, Set-WendmemDb, and Invoke-Wm.
# . "$PSScriptRoot\_Resolve-Wendmem.ps1"
#
# After calling Resolve-WendmemExe the script-scope $WendmemDir is set automatically.
# Use Invoke-Wm instead of & $WendmemExe — it changes to $WendmemDir first so that
# appsettings.json is found correctly regardless of where the script is invoked from.

function Resolve-WendmemExe([string]$Exe = '')
{
    if ($Exe -and (Test-Path $Exe))
    {
        $resolved = (Resolve-Path $Exe).Path
        Set-Variable -Name WendmemDir -Value (Split-Path $resolved -Parent) -Scope 1
        return $resolved
    }
    $found = Get-Command wendmem -ErrorAction SilentlyContinue
    if ($found)
    {
        Set-Variable -Name WendmemDir -Value (Split-Path $found.Source -Parent) -Scope 1
        return $found.Source
    }
    # Search order: standard install location, then relative paths from repo root.
    $hit = @(
        'C:\tools\wendmem\Wendmem.exe'    # standard install location
        "$PSScriptRoot\..\..\Wendmem.exe"
        "$PSScriptRoot\..\..\wendmem.exe"
        "$PSScriptRoot\..\..\src\Wendmem\bin\Release\net10.0\win-x64\publish\wendmem.exe"
        "$PSScriptRoot\..\..\src\Wendmem\bin\Debug\net10.0\win-x64\publish\wendmem.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($hit)
    {
        $resolved = (Resolve-Path $hit).Path
        Set-Variable -Name WendmemDir -Value (Split-Path $resolved -Parent) -Scope 1
        return $resolved
    }
    Write-Error "Cannot find wendmem.exe. Add it to PATH or run: dotnet publish src/Wendmem -c Release -r win-x64"
    exit 1
}

function Set-WendmemDb([string]$DbPath = '')
{
    if ($DbPath)
    { return ($env:WENDMEM_DB = $DbPath)
    }
    if ($env:WENDMEM_DB)
    { return $env:WENDMEM_DB
    }
    # Prefer palace.duckdb beside the resolved exe (standard production layout)
    $beside = Join-Path $WendmemDir 'palace.duckdb'
    if (Test-Path $beside)
    { return ($env:WENDMEM_DB = $beside)
    }
    # Dev/CI fallback: use a temp DB (no existing palace beside the exe)
    $p = Join-Path ([IO.Path]::GetTempPath()) "wendmem-$([guid]::NewGuid().ToString('N').Substring(0,8)).duckdb"
    return ($env:WENDMEM_DB = $p)
}

# Run wendmem from its own directory so appsettings.json is always found.
# Usage: Invoke-Wm prune --wing work
#        $out = Invoke-Wm search "query" 2>&1
function Invoke-Wm
{
    Push-Location $WendmemDir
    try
    { & $WendmemExe @args
    } finally
    { Pop-Location
    }
}
