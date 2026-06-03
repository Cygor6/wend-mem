#Requires -Version 7.4
<#
.SYNOPSIS
    Installs wendmem integrations for Claude Code, Gemini CLI, Codex, and Goose.

.DESCRIPTION
    Copies hook scripts, configuration files, and a lean per-project instruction
    file to the correct locations for each AI agent. Designed to be safe to run
    repeatedly: it keeps a single .bak per touched file (never accumulates
    timestamped backups) and only rewrites when content actually changes.

    Token model: the full wendmem protocol stays in one place (the installed
    SKILL.md + references/tools.md). Each project root gets a *lean* agent file
    (CLAUDE.md / GEMINI.md / AGENTS.md) that names the wing + code root and the
    session contract, and points at references/tools.md for detail — progressive
    disclosure rather than copying the whole skill into every project.

.PARAMETER WendmemDir
    Folder where Wendmem.exe and integrations/ live.
    Default: C:\tools\wendmem (standard install location — override if installed elsewhere).

.PARAMETER Port
    Port wendmem listens on. Default: 5133.

.PARAMETER ProjectDir
    Project directory for project-scoped files. Default: current directory.

.PARAMETER Wing
    Wing name for the project. Default: project directory name.

.PARAMETER CodeRoot
    Default codebase root. Default: C:\dev (override per-project via .wendmem-code-root marker or WENDMEM_CODE_ROOT env var).

.PARAMETER Agents
    Which agents to install for. Default: all.
    Possible values: ClaudeCode, GeminiCLI, Codex, Goose

.EXAMPLE
    .\Setup-Integrations.ps1
#>
[CmdletBinding()]
param(
    [string]   $WendmemDir  = 'C:\tools\wendmem',  # standard install location; override with -WendmemDir
    [int]      $Port        = 5133,
    [string]   $ProjectDir  = $PWD.Path,
    [string]   $Wing        = '',
    [string]   $CodeRoot    = 'C:\dev',
    [string[]] $Agents      = @('ClaudeCode', 'GeminiCLI', 'Codex', 'Goose'),
    [switch]   $Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# File tracking
# ---------------------------------------------------------------------------
$Script:DirsCreated  = [System.Collections.Generic.List[string]]::new()
$Script:FilesWritten = [System.Collections.Generic.List[string]]::new()
$Script:FilesSkipped = [System.Collections.Generic.List[string]]::new()
$Script:FilesWarned  = [System.Collections.Generic.List[string]]::new()

function Write-Step { param([string]$Msg) Write-Host "`n▶ $Msg" -ForegroundColor Cyan }
function Write-OK   { param([string]$Msg) Write-Host "  ✓ $Msg" -ForegroundColor Green }
function Write-Skip { param([string]$Msg) Write-Host "  · $Msg" -ForegroundColor Gray }
function Write-Warn { param([string]$Msg) Write-Host "  ⚠ $Msg" -ForegroundColor Yellow }

function Ensure-Dir {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        $Script:DirsCreated.Add($Path)
    }
}

# Single overwriting backup — running N times leaves exactly one .bak, not N.
function Backup-File {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return }
    $backup = "$Path.bak"
    Copy-Item -Path $Path -Destination $backup -Force
    Write-Skip "Backup: $backup"
}

function Test-CommandExists {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

# Writes only if content changed; backs up only when overwriting a changed file.
function Set-TextFileIfChanged {
    param([string]$Path, [string]$Content)
    if (Test-Path $Path) {
        $old = [string](Get-Content $Path -Raw -Encoding UTF8)
        if ($old -eq $Content) {
            Write-Skip "Unchanged: $Path"
            $Script:FilesSkipped.Add($Path)
            return
        }
        Backup-File $Path
    }
    Set-Content -Path $Path -Value $Content -Encoding UTF8 -Force
    Write-OK "Created/updated: $Path"
    $Script:FilesWritten.Add($Path)
}

function Set-FileContent {
    param([string]$Path, [string]$Content)
    Set-TextFileIfChanged -Path $Path -Content $Content
}

function Copy-Safe {
    param([string]$Path, [string]$Destination)
    if (-not (Test-Path $Path)) {
        Write-Warn "Source missing: $Path"
        $Script:FilesWarned.Add($Path)
        return
    }
    Copy-Item -Path $Path -Destination $Destination -Force
    Write-OK "Copied $(Split-Path $Path -Leaf) → $Destination"
    $Script:FilesWritten.Add($Destination)
}

function Merge-HashtableDeep {
    param([hashtable]$Base, [hashtable]$Overlay)
    foreach ($key in $Overlay.Keys) {
        if ($Base.ContainsKey($key) -and $Base[$key] -is [hashtable] -and $Overlay[$key] -is [hashtable]) {
            Merge-HashtableDeep -Base $Base[$key] -Overlay $Overlay[$key]
        } else {
            $Base[$key] = $Overlay[$key]
        }
    }
}

# Idempotent JSON merge: backs up + writes only when the serialized result
# differs from the (reparsed) existing content, so re-runs are no-ops.
function Merge-JsonFile {
    param([string]$Path, [hashtable]$NewContent)

    $existing = @{}
    $oldNorm  = $null
    if (Test-Path $Path) {
        $raw = [string](Get-Content $Path -Raw -Encoding UTF8)
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            try {
                $existing = $raw | ConvertFrom-Json -AsHashtable
                $oldNorm  = ($existing | ConvertTo-Json -Depth 50)
            } catch {
                throw "Invalid JSON in $Path. File not modified. Error: $($_.Exception.Message)"
            }
        }
    }

    Merge-HashtableDeep -Base $existing -Overlay $NewContent
    $newNorm = $existing | ConvertTo-Json -Depth 50

    if ($null -ne $oldNorm -and $oldNorm -eq $newNorm) {
        Write-Skip "Unchanged: $Path"
        $Script:FilesSkipped.Add($Path)
        return
    }

    if (Test-Path $Path) { Backup-File $Path }
    $newNorm | Set-Content $Path -Encoding UTF8 -Force
    Write-OK "Updated idempotently: $Path"
    $Script:FilesWritten.Add($Path)
}

function Set-CodexToml {
    param([string]$Path, [int]$Port)

    $orig = if (Test-Path $Path) { [string](Get-Content $Path -Raw -Encoding UTF8) } else { '' }
    $content = $orig

    $mcpBlock = @"
[mcp_servers.wendmem]
url = "http://localhost:${Port}/mcp"
startup_timeout_sec = 10
tool_timeout_sec = 60
enabled = true
"@

    $featuresKeys = @{ 'codex_hooks' = 'true'; 'memories' = 'false' }

    # Replace any existing wendmem mcp block, then re-append once.
    $content = [regex]::Replace($content, '(?ms)^\[mcp_servers\.wendmem\]\s.*?(?=^\[|\z)', '')
    $content = $content.TrimEnd() + "`r`n`r`n" + $mcpBlock.TrimEnd() + "`r`n"

    if ($content -match '(?m)^\[features\]') {
        foreach ($k in $featuresKeys.Keys) {
            if ($content -match "(?m)^$k\s*=") {
                $content = [regex]::Replace($content, "(?m)^$k\s*=.*$", "$k = $($featuresKeys[$k])")
            } else {
                $content = [regex]::Replace($content, '(?ms)^\[features\].*?(?=^\[|\z)', { param($m) $m.Value.TrimEnd() + "`r`n$k = $($featuresKeys[$k])`r`n" })
            }
        }
    } else {
        $content = $content.TrimEnd() + "`r`n`r`n[features]`r`ncodex_hooks = true`r`nmemories = false`r`n"
    }

    $content = $content.TrimEnd() + "`r`n"
    if ($content -eq $orig) {
        Write-Skip "Unchanged: $Path"; $Script:FilesSkipped.Add($Path); return
    }
    if (Test-Path $Path) { Backup-File $Path }
    Set-Content -Path $Path -Value $content -Encoding UTF8 -Force
    Write-OK "Codex config.toml updated: $Path"
    $Script:FilesWritten.Add($Path)
}

# Safe Goose merge: removes ONLY the wendmem block and reinserts it.
# Never touches url:/enabled: of other extensions.
function Set-GooseConfig {
    param([string]$Path, [int]$Port)

    $wendmemBlock = @(
        '  wendmem:',
        '    name: wendmem',
        '    type: streamable_http',
        "    url: http://localhost:${Port}/mcp",
        '    enabled: true',
        '    timeout: 300'
    ) -join "`r`n"

    if (-not (Test-Path $Path)) {
        Set-TextFileIfChanged $Path ("extensions:`r`n" + $wendmemBlock + "`r`n")
        return
    }

    $orig = [string](Get-Content $Path -Raw -Encoding UTF8)

    # Strip an existing wendmem block: the 'wendmem:' line plus all lines
    # indented 4+ spaces beneath it. Leaves every other extension untouched.
    $content = [regex]::Replace($orig, '(?m)^  wendmem:\r?\n(?:    .*(?:\r?\n|$))*', '')

    if ($content -match '(?m)^extensions:[ \t]*\r?$') {
        $content = [regex]::Replace($content, '(?m)^extensions:[ \t]*\r?$', "extensions:`r`n$wendmemBlock", 1)
    } elseif ($content -match '(?m)^extensions:') {
        $content = [regex]::Replace($content, '(?m)^(extensions:.*)$', "`$1`r`n$wendmemBlock", 1)
    } else {
        $content = $content.TrimEnd() + "`r`n`r`nextensions:`r`n$wendmemBlock`r`n"
    }

    $content = $content.TrimEnd() + "`r`n"
    if ($content -eq $orig) {
        Write-Skip "Goose config unchanged: $Path"; $Script:FilesSkipped.Add($Path); return
    }
    Backup-File $Path
    Set-Content -Path $Path -Value $content -Encoding UTF8 -Force
    Write-OK "Goose wendmem extension updated: $Path"
    $Script:FilesWritten.Add($Path)
}

# ---------------------------------------------------------------------------
# Paths & validation
# ---------------------------------------------------------------------------
$IntegrationsDir = Join-Path $WendmemDir 'integrations'
$HooksDir        = Join-Path $IntegrationsDir 'hooks'
$InstallDir      = $PSScriptRoot
$CentralSkillMd  = Join-Path $IntegrationsDir 'SKILL.md'
$CentralToolsMd  = Join-Path $IntegrationsDir 'references\tools.md'
$AgentTemplate   = Join-Path $InstallDir 'agent-instructions.tmpl.md'

if (-not (Test-Path (Join-Path $WendmemDir 'Wendmem.exe'))) {
    Write-Error "Wendmem.exe not found in: $WendmemDir"; exit 1
}
if (-not (Test-Path $CentralSkillMd)) {
    Write-Error "Central SKILL.md missing: $CentralSkillMd"; exit 1
}
if (-not (Test-Path $CentralToolsMd)) {
    Write-Error "references\tools.md missing: $CentralToolsMd"; exit 1
}
if (-not (Test-Path $AgentTemplate)) {
    Write-Error "agent-instructions.tmpl.md missing next to this script: $AgentTemplate"; exit 1
}
if (-not $Wing) { $Wing = Split-Path $ProjectDir -Leaf }

Write-Host "`n═══════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host " Wendmem Integration Setup" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  WendmemDir : $WendmemDir"
Write-Host "  Port       : $Port"
Write-Host "  ProjectDir : $ProjectDir"
Write-Host "  Wing       : $Wing"
Write-Host "  CodeRoot   : $CodeRoot"
Write-Host "  Agents     : $($Agents -join ', ')"
Write-Host "═══════════════════════════════════════════════════"

# ---------------------------------------------------------------------------
# 1. Hook scripts
# ---------------------------------------------------------------------------
Write-Step "Installing hook scripts → $HooksDir"
Ensure-Dir $HooksDir
Copy-Safe (Join-Path $InstallDir 'wakeup-hook.mjs') (Join-Path $HooksDir 'wakeup-hook.mjs')
Copy-Safe (Join-Path $InstallDir 'stop-hook.mjs')   (Join-Path $HooksDir 'stop-hook.mjs')

if (Test-CommandExists 'node') {
    Write-OK "Node.js available: $(node --version 2>&1)"
} else {
    Write-Warn "Node.js not found in PATH. Hook scripts require Node.js 18+."
}

# ---------------------------------------------------------------------------
# 2. Project markers
# ---------------------------------------------------------------------------
$wingFile = Join-Path $ProjectDir '.wendmem-wing'
if (-not (Test-Path $wingFile)) {
    Write-Step "Creating .wendmem-wing ($Wing)"
    Set-FileContent $wingFile $Wing
} else {
    $currentWing = Get-Content $wingFile -TotalCount 1
    if ($currentWing -ne $Wing) {
        if ($Clean) {
            Write-Step "Updating .wendmem-wing ($currentWing → $Wing)"
            Set-FileContent $wingFile $Wing
        } else {
            Write-Warn ".wendmem-wing differs: '$currentWing' ≠ '$Wing'. Use -Clean to update."
        }
    } else {
        Write-Skip ".wendmem-wing already set: $currentWing"
    }
}

$codeRootFile = Join-Path $ProjectDir '.wendmem-code-root'
if (-not (Test-Path $codeRootFile)) {
    Write-Step "Creating .wendmem-code-root ($CodeRoot)"
    Set-FileContent $codeRootFile $CodeRoot
} else {
    $currentCodeRoot = Get-Content $codeRootFile -TotalCount 1
    if ($currentCodeRoot -ne $CodeRoot) {
        Write-Step "Updating .wendmem-code-root ($currentCodeRoot → $CodeRoot)"
        Set-FileContent $codeRootFile $CodeRoot
    } else {
        Write-Skip ".wendmem-code-root already set: $currentCodeRoot"
    }
}

# ---------------------------------------------------------------------------
# Lean per-project agent file from the template (NOT a full SKILL.md copy)
# ---------------------------------------------------------------------------
function Install-AgentInstructions {
    param([string]$DestinationPath)

    $tmpl = [string](Get-Content $AgentTemplate -Raw -Encoding UTF8)
    $tmpl = $tmpl.Replace('{{WING}}', $Wing).Replace('{{CODE_ROOT}}', $CodeRoot)
    Set-TextFileIfChanged -Path $DestinationPath -Content $tmpl

    # Full tool reference for progressive disclosure, alongside the agent file.
    $referencesDir = Join-Path (Split-Path $DestinationPath -Parent) 'references'
    Ensure-Dir $referencesDir
    Copy-Safe $CentralToolsMd (Join-Path $referencesDir 'tools.md')
}

# ---------------------------------------------------------------------------
# 3. Claude Code
# ---------------------------------------------------------------------------
if ($Agents -contains 'ClaudeCode') {
    Write-Step "Claude Code"
    $claudeUserDir = Join-Path $env:USERPROFILE '.claude'
    Ensure-Dir $claudeUserDir

    $settingsPath = Join-Path $claudeUserDir 'settings.json'
    $hooksBlock = @{
        hooks = @{
            SessionStart = @(
                @{ matcher = 'startup'; hooks = @(@{ type = 'command'; command = "node $HooksDir\wakeup-hook.mjs"; timeout = 30 }) },
                @{ matcher = 'compact'; hooks = @(@{ type = 'command'; command = "node $HooksDir\wakeup-hook.mjs"; timeout = 30 }) }
            )
            Stop = @(
                @{ hooks = @(@{ type = 'command'; command = "node $HooksDir\stop-hook.mjs"; async = $true; timeout = 10 }) }
            )
        }
    }
    Merge-JsonFile $settingsPath $hooksBlock
    Install-AgentInstructions -DestinationPath (Join-Path $ProjectDir 'CLAUDE.md')

    Write-OK "Claude Code configuration complete."
    Write-Skip "Register the MCP server if not done:"
    Write-Skip "  claude mcp add --transport http wendmem http://localhost:${Port}/mcp"
}

# ---------------------------------------------------------------------------
# 4. Gemini CLI  (SessionStart only — no per-turn WakeUp)
# ---------------------------------------------------------------------------
if ($Agents -contains 'GeminiCLI') {
    Write-Step "Gemini CLI"
    $geminiUserDir = Join-Path $env:USERPROFILE '.gemini'
    Ensure-Dir $geminiUserDir

    $geminiSettings = Join-Path $geminiUserDir 'settings.json'
    $geminiBlock = @{
        mcpServers   = @{ wendmem = @{ httpUrl = "http://localhost:${Port}/mcp"; timeout = 60000 } }
        hooksEnabled = $true
        hooks = @{
            SessionStart = @(
                @{ matcher = 'startup'; hooks = @(@{ name = 'wendmem-wakeup'; type = 'command'; command = "node $HooksDir\wakeup-hook.mjs"; timeout = 30000 }) }
            )
            SessionEnd = @(
                @{ matcher = 'exit'; hooks = @(@{ name = 'wendmem-distill-reminder'; type = 'command'; command = "node $HooksDir\stop-hook.mjs"; timeout = 10000 }) }
            )
        }
    }
    Merge-JsonFile $geminiSettings $geminiBlock
    Install-AgentInstructions -DestinationPath (Join-Path $ProjectDir 'GEMINI.md')

    Write-OK "Gemini CLI configuration complete."
}

# ---------------------------------------------------------------------------
# 5. Codex
# ---------------------------------------------------------------------------
if ($Agents -contains 'Codex') {
    Write-Step "Codex"
    $codexUserDir = Join-Path $env:USERPROFILE '.codex'
    Ensure-Dir $codexUserDir

    Set-CodexToml -Path (Join-Path $codexUserDir 'config.toml') -Port $Port

    $codexHooks = @{
        hooks = @{
            SessionStart = @(
                @{ matcher = 'startup'; hooks = @(@{ type = 'command'; command = "node $HooksDir\wakeup-hook.mjs"; timeout = 30 }) }
            )
            Stop = @(
                @{ hooks = @(@{ type = 'command'; command = "node $HooksDir\stop-hook.mjs"; timeout = 10 }) }
            )
        }
    }
    Merge-JsonFile (Join-Path $codexUserDir 'hooks.json') $codexHooks
    Install-AgentInstructions -DestinationPath (Join-Path $ProjectDir 'AGENTS.md')

    Write-OK "Codex configuration complete."
    Write-Skip "Verify MCP server: codex mcp list"
}

# ---------------------------------------------------------------------------
# 6. Goose
# ---------------------------------------------------------------------------
if ($Agents -contains 'Goose') {
    Write-Step "Goose"
    $gooseConfigDir = Join-Path $env:APPDATA 'goose'
    Ensure-Dir $gooseConfigDir
    Set-GooseConfig -Path (Join-Path $gooseConfigDir 'config.yaml') -Port $Port

    $pluginDir     = Join-Path $env:USERPROFILE '.agents\plugins\wendmem'
    $pluginHookDir = Join-Path $pluginDir 'hooks'
    $pluginScripts = Join-Path $pluginDir 'scripts'
    $pluginRefsDir = Join-Path $pluginDir 'references'
    Ensure-Dir $pluginHookDir
    Ensure-Dir $pluginScripts
    Ensure-Dir $pluginRefsDir

    # Goose has Stop and SessionEnd as separate events — wire both to the
    # reminder so it fires on normal session end as well as explicit stop.
    $hooksJson = @'
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "node ${PLUGIN_ROOT}/scripts/wakeup-hook.mjs", "timeout": 30 } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "node ${PLUGIN_ROOT}/scripts/stop-hook.mjs", "timeout": 10 } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "command", "command": "node ${PLUGIN_ROOT}/scripts/stop-hook.mjs", "timeout": 10 } ] }
    ]
  }
}
'@
    Set-TextFileIfChanged (Join-Path $pluginHookDir 'hooks.json') $hooksJson

    Copy-Safe (Join-Path $HooksDir 'wakeup-hook.mjs') (Join-Path $pluginScripts 'wakeup-hook.mjs')
    Copy-Safe (Join-Path $HooksDir 'stop-hook.mjs')   (Join-Path $pluginScripts 'stop-hook.mjs')
    Copy-Safe $CentralToolsMd                          (Join-Path $pluginRefsDir 'tools.md')

    # Lean recipe. Goose does not feed hook stdout back to the model, so the
    # recipe (not the hook) owns WakeUp context injection for Goose.
    $recipeContent = @"
version: 1.0
title: "Wendmem session start"
description: "Loads wendmem context and pins the session to wing: $Wing"
extensions:
  - name: wendmem
    type: streamable_http
    url: http://localhost:${Port}/mcp

instructions: |
  Active wing for this project: $Wing. Code root: $CodeRoot.
  Use wing: "$Wing" on every wendmem call this session. Never mix wings.

  Start with:
    WakeUp(wing: "$Wing", seedQuery: "<what you are working on today>")
  seedQuery is required to surface the semantic layer, past episodes, and skills.
  Read palace://schema if the resource exists; otherwise continue.

  Ground project-specific claims in retrieved drawers or wiki pages, not training data.
  Exact symbol/error/ID -> GrepExact. Concept/topic -> SearchMemories.
  Full tool parameters: references/tools.md (load on demand).

  End every non-trivial session in this fixed order:
    1. RecordEpisode(wing: "$Wing", goal, plan, outcome, whatWorked, whatFailed, nextTime)
    2. Distill(wing: "$Wing", sessionSummary: "<one paragraph about what was done>")
    3. WikiWrite(...) with real citation drawer IDs if Distill returns a scaffold.
  Skip all three for trivial Q&A.
"@
    Set-FileContent (Join-Path $ProjectDir 'wendmem-session.yaml') $recipeContent

    Write-OK "Goose configuration complete."
    Write-Skip "Plugin installed: $pluginDir"
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host "`n═══════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host " Summary" -ForegroundColor Magenta
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "  Dirs created : $($Script:DirsCreated.Count)"
Write-Host "  Files written: $($Script:FilesWritten.Count)"
Write-Host "  Skipped      : $($Script:FilesSkipped.Count)"
if ($Script:FilesWarned.Count -gt 0) {
    Write-Warn "Missing sources ($($Script:FilesWarned.Count)):"
    foreach ($f in $Script:FilesWarned) { Write-Warn "  $f" }
}
Write-Host "`nDone. Start the wendmem server, then launch any configured agent.`n" -ForegroundColor Green
