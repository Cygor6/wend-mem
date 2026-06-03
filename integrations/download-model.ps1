#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads the EmbeddingGemma-300M quantized ONNX model and SentencePiece tokenizer.

.DESCRIPTION
    Files are downloaded from Hugging Face (onnx-community/embeddinggemma-300m-ONNX).
    By default, they are saved into ..\models\embeddinggemma\ relative to this script.
    The quantized (int8) model is ~295 MB - suitable for on-device CPU inference via ONNX Runtime.

    After running this script, run build.ps1 to copy the model into publish/.

    Environment variables (optional overrides):
      WENDMEM_MODEL_PATH     - path to model_quantized.onnx
      WENDMEM_TOKENIZER_PATH - path to tokenizer.model

.PARAMETER OutDir
    The directory to download the model files into. Defaults to ..\models\embeddinggemma\
#>

[CmdletBinding()]
param(
    [Parameter(HelpMessage="The directory to download the model files into.")]
    [string]$OutDir = (Join-Path $PSScriptRoot "..\models\embeddinggemma")
)

$ErrorActionPreference = 'Stop'

$modelsDir = $OutDir
$hfRepo    = "https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/resolve/main"

$downloads = @(
    @{ Url = "$hfRepo/onnx/model_quantized.onnx";      File = "model_quantized.onnx" }
    @{ Url = "$hfRepo/onnx/model_quantized.onnx_data"; File = "model_quantized.onnx_data" }
    @{ Url = "$hfRepo/tokenizer.model";                 File = "tokenizer.model" }
    @{ Url = "$hfRepo/config.json";                     File = "config.json" }
)

if (-not (Test-Path $modelsDir)) {
    Write-Host "Creating directory: $modelsDir" -ForegroundColor Gray
    New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
}

foreach ($item in $downloads) {
    $outPath = Join-Path $modelsDir $item.File
    if ((Test-Path $outPath) -and ($item.File -ne "config.json")) {
        Write-Host "  Already exists: $($item.File)" -ForegroundColor DarkGray
    } else {
        Write-Host "Downloading $($item.File) ..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $item.Url -OutFile $outPath -UseBasicParsing
    }
}

Write-Host ""
Write-Host "Done. Model files in ${modelsDir}:" -ForegroundColor Green
Get-ChildItem $modelsDir | ForEach-Object {
    Write-Host ("  {0,-30} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}

Write-Host ""
Write-Host "Run ../build.ps1 to build and copy into publish/." -ForegroundColor Green
