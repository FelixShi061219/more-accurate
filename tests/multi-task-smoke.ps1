param([string]$PreviewDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('more-accurate-previews-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('more-accurate-multi-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $csc
$sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'app') -Filter '*.cs' | ForEach-Object { $_.FullName })
$check = Join-Path $testRoot 'multi-check.exe'
& $csc /nologo /target:exe /main:MultiCheck /out:$check /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll ('/reference:' + (Join-Path $framework 'WPF\UIAutomationClient.dll')) ('/reference:' + (Join-Path $framework 'WPF\UIAutomationTypes.dll')) ('/reference:' + (Join-Path $framework 'WPF\WindowsBase.dll')) $sources (Join-Path $PSScriptRoot 'multi-task-smoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Multi-task test compilation failed.' }
& $check $PreviewDirectory
if ($LASTEXITCODE -ne 0) { throw ('Multi-task verification failed. Isolated test data: ' + $testRoot) }
# Retain isolated diagnostics for inspection. No installed files or registry settings are changed.
Write-Output ('Isolated test data: ' + $testRoot)
