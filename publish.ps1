#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build, test, and publish wendmem.

.DESCRIPTION
    Stages: clean, restore, test, publish, copy-content.

    Usage:
      ./build.ps1                     # full: clean + test + publish
      ./build.ps1 -SkipTests          # skip tests
      ./build.ps1 -SkipClean          # skip clean
      ./build.ps1 -Runtime linux-x64  # cross-publish

.PARAMETER SkipTests
    Skip the test stage.

.PARAMETER SkipClean
    Skip the clean stage.

.PARAMETER Runtime
    Target runtime identifier (default: win-x64).

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Output
    Publish output folder (default: ./publish).
#>

[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipClean,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "publish")
)

$ErrorActionPreference = "Stop"

$solution  = Join-Path $PSScriptRoot "wendmem.slnx"
$project   = Join-Path $PSScriptRoot "src\Wendmem\Wendmem.csproj"
$tests     = Join-Path $PSScriptRoot "tests\Wendmem.Tests\Wendmem.Tests.csproj"

# --- Content to copy after publish -------------------------------------------
# Add entries here as needed.  Each is @{ From = "src path"; To = "dest folder" }
$content = @(
    @{ From = Join-Path $PSScriptRoot "skills";           To = "skills" },
    @{ From = Join-Path $PSScriptRoot "models";           To = "models" },
    @{ From = Join-Path $PSScriptRoot "integrations";     To = "integrations" }
)

# --- Stage: Clean ------------------------------------------------------------
if (-not $SkipClean)
{
    Write-Host "`n[clean] Removing bin/obj and publish folder ..." -ForegroundColor Cyan
    dotnet clean $solution -c $Configuration --verbosity quiet 2>&1 | Out-Null
    if (Test-Path $Output)
    { Remove-Item -Recurse -Force $Output
    }
}

# --- Stage: Test -------------------------------------------------------------
if (-not $SkipTests)
{
    Write-Host "`n[test] Running tests ..." -ForegroundColor Cyan
    dotnet test $tests -c $Configuration --verbosity normal
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "[test] FAILED — aborting publish." -ForegroundColor Red
        exit 1
    }
}

# --- Stage: Publish ----------------------------------------------------------
Write-Host "`n[publish] Publishing $Runtime ($Configuration) ..." -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    -o $Output

if ($LASTEXITCODE -ne 0)
{
    Write-Host "[publish] FAILED." -ForegroundColor Red
    exit 1
}

# --- Stage: Copy content -----------------------------------------------------
Write-Host "`n[content] Copying extra files to publish folder ..." -ForegroundColor Cyan
foreach ($item in $content)
{
    $dest = Join-Path $Output $item.To
    if (Test-Path $item.From)
    {
        Copy-Item -Path $item.From -Destination $dest -Recurse -Force
        Write-Host "  + $($item.To)" -ForegroundColor Green
    } else
    {
        Write-Host "  ? $($item.To) — source not found, skipped" -ForegroundColor Yellow
    }
}

Write-Host "`n[done] Published to: $Output" -ForegroundColor Green
