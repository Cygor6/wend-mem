#Requires -Version 7.5
<#
.SYNOPSIS
    Full wendmem optimization pipeline.
.DESCRIPTION
    Steps: prune → rescore → kg-resolve → calibrate → kg-eval → skill-opt → reflect → wiki lint
.EXAMPLE
    # Basic run — safe defaults, no config write
    pwsh ./Invoke-WendmemOptimize.ps1 -Wing work

    # Persist calibrated thresholds and optimize a skill
    pwsh ./Invoke-WendmemOptimize.ps1 -Wing work -Skill ./skills/coding/SKILL.md -WriteConfig

    # Use LLM rescore, skip lint
    pwsh ./Invoke-WendmemOptimize.ps1 -Wing work -LlmRescore -SkipLint
#>
param(
    [Parameter(Mandatory)] [string] $Wing,

    # Skill optimization (skipped when empty)
    [string] $Skill         = '',   # path to SKILL.md to optimize
    [int]    $SkillEpochs   = 3,
    [int]    $SkillBudget   = 3,

    # Tuning
    [double] $PruneThreshold = 0.97,
    [int]    $CalibSamples   = 200,
    [int]    $EvalQuestions  = 20,

    [switch] $WriteConfig,          # persist calibrated thresholds to palace-config.json
    [switch] $LlmRescore,           # LLM-based rescoring (slower, more accurate)
    [switch] $SkipReflect,
    [switch] $SkipLint,

    [string] $WendmemExe = '',
    [string] $DbPath     = ''
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_Resolve-Wendmem.ps1"

$WendmemExe = Resolve-WendmemExe $WendmemExe   # also sets $WendmemDir
$DbPath     = Set-WendmemDb $DbPath

# ── Step runner ──────────────────────────────────────────────
$stepN       = 0
$stepResults = [System.Collections.Generic.List[pscustomobject]]::new()

function Invoke-WmStep {
    param([string]$Name, [string[]]$CmdArgs, [bool]$Skip = $false)
    $script:stepN++
    Write-Host ''
    Write-Host "  [$($script:stepN)] $Name" -ForegroundColor Cyan
    if ($Skip) {
        Write-Host '      — skipped' -ForegroundColor DarkGray
        $script:stepResults.Add([pscustomobject]@{ Name = $Name; Status = 'skip' })
        return
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Push-Location $script:WendmemDir
    try { & $script:WendmemExe @CmdArgs }
    finally { Pop-Location }
    $ec = $LASTEXITCODE
    $sw.Stop()
    Write-Host "      ($($sw.Elapsed.TotalSeconds.ToString('F1'))s)" -ForegroundColor DarkGray
    $status = if ($ec -eq 0) { 'ok' } else { 'fail' }
    $script:stepResults.Add([pscustomobject]@{ Name = $Name; Status = $status })
    if ($ec -ne 0) { Write-Warning "$Name exited $ec — continuing pipeline." }
}

# ── Banner ───────────────────────────────────────────────────
Write-Host ''
Write-Host '╔════════════════════════════════╗' -ForegroundColor Cyan
Write-Host '║   wendmem optimize pipeline    ║' -ForegroundColor Cyan
Write-Host '╚════════════════════════════════╝' -ForegroundColor Cyan
Write-Host "  wing : $Wing"       -ForegroundColor DarkGray
Write-Host "  exe  : $WendmemExe" -ForegroundColor DarkGray
Write-Host "  db   : $DbPath"     -ForegroundColor DarkGray
if ($Skill) {
    Write-Host "  skill: $Skill  (epochs=$SkillEpochs  budget=$SkillBudget)" -ForegroundColor DarkGray
}
Write-Host ''

# ── 1. Prune — deduplicate near-identical drawers ────────────
Invoke-WmStep 'prune' @('prune', '--wing', $Wing, '--threshold', "$PruneThreshold")

# ── 2. Rescore — refresh importance scores ───────────────────
$rescoreArgs = @('rescore', '--wing', $Wing)
if ($LlmRescore) { $rescoreArgs += '--llm' }
Invoke-WmStep 'rescore' $rescoreArgs

# ── 3. KG resolve — merge duplicate entities, normalize predicates
Invoke-WmStep 'kg-resolve' @('kg-resolve', '--wing', $Wing)

# ── 4. Calibrate — tune retrieval thresholds ─────────────────
$calibArgs = @('calibrate', '--wing', $Wing, '--samples', "$CalibSamples")
if ($WriteConfig) { $calibArgs += '--write-config' }
Invoke-WmStep 'calibrate' $calibArgs

# ── 5. KG eval — measure retrieval quality ───────────────────
Invoke-WmStep 'kg-eval' @('kg-eval', '--wing', $Wing, '--questions', "$EvalQuestions")

# ── 6. Skill-opt — optimize SKILL.md (conditional) ──────────
$skillArgs = @('skill-opt', '--wing', $Wing, '--skill', $Skill, '--epochs', "$SkillEpochs", '--budget', "$SkillBudget")
Invoke-WmStep 'skill-opt' $skillArgs -Skip:(-not $Skill)

# ── 7. Reflect — surface reflection drafts ───────────────────
Invoke-WmStep 'reflect' @('reflect', 'run', '--wing', $Wing, '--write') -Skip:$SkipReflect.IsPresent

# ── 8. Wiki lint — check wiki health ─────────────────────────
Invoke-WmStep 'wiki lint' @('wiki', 'lint', '--wing', $Wing) -Skip:$SkipLint.IsPresent

# ── Summary ──────────────────────────────────────────────────
Write-Host ''
Write-Host '─── Results ─────────────────────────' -ForegroundColor Cyan
foreach ($r in $stepResults) {
    $color = switch ($r.Status) { 'ok' { 'Green' } 'fail' { 'Red' } default { 'DarkGray' } }
    $icon  = switch ($r.Status) { 'ok' { [char]0x2714 } 'fail' { [char]0x2718 } default { '-' } }
    Write-Host "  $icon $($r.Name)" -ForegroundColor $color
}
Write-Host ''

$nOk   = @($stepResults | Where-Object Status -eq 'ok').Count
$nFail = @($stepResults | Where-Object Status -eq 'fail').Count
$nSkip = @($stepResults | Where-Object Status -eq 'skip').Count

if ($nFail -gt 0) {
    Write-Host "  $nOk ok  ·  $nFail failed  ·  $nSkip skipped" -ForegroundColor Red
    exit 1
} else {
    Write-Host "  $nOk ok  ·  $nSkip skipped  —  all done." -ForegroundColor Green
}
