$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$source = Join-Path $root 'MoreAccurateIndicator.cs'
$output = Join-Path $root 'MoreAccurateIndicator.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw 'Windows .NET Framework compiler not found.' }
$iconResource = '/resource:' + (Join-Path $root 'icons\openai.png') + ',openai.png'
$applicationIcon = '/win32icon:' + (Join-Path $root 'icons\app.ico')
$framework = Split-Path -Parent $csc
$uiaClient = '/reference:' + (Join-Path $framework 'WPF\UIAutomationClient.dll')
$uiaTypes = '/reference:' + (Join-Path $framework 'WPF\UIAutomationTypes.dll')
$windowsBase = '/reference:' + (Join-Path $framework 'WPF\WindowsBase.dll')
& $csc /nologo /target:winexe /optimize+ /out:$output /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll $uiaClient $uiaTypes $windowsBase $iconResource $applicationIcon $source (Join-Path $root 'CodexTelemetry.cs') (Join-Path $root 'SessionHub.cs') (Join-Path $root 'DesktopBridge.cs') (Join-Path $root 'NativeBridge.cs') (Join-Path $root 'TaskWheel.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $output
