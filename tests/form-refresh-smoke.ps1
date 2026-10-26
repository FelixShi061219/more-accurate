$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('more-accurate-form-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$app = Join-Path $testRoot 'MoreAccurateIndicator.exe'
Copy-Item -LiteralPath (Join-Path $projectRoot 'app\MoreAccurateIndicator.exe') -Destination $app
$check = Join-Path $testRoot 'form-refresh-smoke.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /out:$check /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'form-refresh-smoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$previousCodexHome = $env:CODEX_HOME
$env:CODEX_HOME = Join-Path $testRoot 'codex'
try { & $check $app } finally { $env:CODEX_HOME = $previousCodexHome }
if ($LASTEXITCODE -ne 0) { throw ('Form refresh verification failed. Diagnostics: ' + $testRoot) }
# Remove only the exact, known files produced by this isolated test.
foreach ($file in @('data\state.json', 'data\task-progress.json', 'data\preferences.json', 'data\display-status.json', 'MoreAccurateIndicator.exe', 'form-refresh-smoke.exe')) {
    Remove-Item -LiteralPath (Join-Path $testRoot $file) -ErrorAction Stop
}
Remove-Item -LiteralPath (Join-Path $testRoot 'data')
Remove-Item -LiteralPath $testRoot
