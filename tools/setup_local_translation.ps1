param(
    [string]$ModelDir = "D:\Models\opus-mt-en-ru-ct2"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $repo ".venv-lt"
$python = Join-Path $venv "Scripts\python.exe"
Write-Host "Setting up offline OPUS-MT translator. One-time internet download required."
if (-not (Test-Path $python)) {
    py -3 -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Python 3 venv creation failed" }
}
& $python -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed" }
& $python -m pip install "ctranslate2>=4.5,<5" "transformers>=4.40,<5" "sentencepiece>=0.2,<0.3" "sacremoses>=0.1" "torch>=2.2"
if ($LASTEXITCODE -ne 0) { throw "Package installation failed" }
& $python (Join-Path $PSScriptRoot "prepare_opus_mt.py") --output $ModelDir
if ($LASTEXITCODE -ne 0) { throw "Offline model preparation failed" }

Write-Host ""
Write-Host "Local model prepared."
Write-Host 'Before running LiveTranscriber in the same PowerShell session, use:'
Write-Host ('$env:LIVE_TRANSLATOR_MODEL_DIR = "' + $ModelDir + '"')
Write-Host ('$env:LIVE_TRANSLATOR_PYTHON = "' + $python + '"')
Write-Host "Groq API key is NOT required for Local OPUS-MT."
