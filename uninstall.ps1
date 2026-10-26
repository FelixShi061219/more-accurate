$ErrorActionPreference = 'Continue'
if (-not $env:CODEX_HOME) { $env:CODEX_HOME = Join-Path $env:USERPROFILE '.codex' }
$root = Split-Path -Parent $PSCommandPath
& (Join-Path $root 'install-browser-bridge.ps1') -Remove
$config = Join-Path $env:CODEX_HOME 'config.toml'
if (Test-Path -LiteralPath $config) {
    $text = [IO.File]::ReadAllText($config)
    $pattern = '(?ms)^# BEGIN MORE ACCURATE LOCAL TELEMETRY\r?\n.*?^# END MORE ACCURATE LOCAL TELEMETRY\r?\n?'
    if ([regex]::IsMatch($text, $pattern)) {
        Copy-Item -LiteralPath $config -Destination ($config + '.before-more-accurate-uninstall.' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
        [IO.File]::WriteAllText($config, [regex]::Replace($text, $pattern, ''), (New-Object Text.UTF8Encoding($false)))
    }
}
. (Join-Path $root 'app\find-codex.ps1')
$codex = Resolve-CodexCli
if ($codex) { & $codex mcp remove more-accurate }
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'More Accurate.lnk'
if (Test-Path -LiteralPath $desktopShortcut) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($desktopShortcut)
    if ($link.TargetPath -eq (Join-Path $root 'app\MoreAccurateIndicator.exe')) { Remove-Item -LiteralPath $desktopShortcut }
}
$skillTarget = Join-Path $env:CODEX_HOME 'skills\more-accurate'
if (Test-Path -LiteralPath $skillTarget) {
    $expected = [IO.Path]::GetFullPath((Join-Path $env:CODEX_HOME 'skills\more-accurate'))
    if ((Resolve-Path -LiteralPath $skillTarget).Path -eq $expected) { Remove-Item -LiteralPath $skillTarget -Recurse -Force }
}
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'More Accurate Indicator.lnk'
if (Test-Path -LiteralPath $startupShortcut) { Remove-Item -LiteralPath $startupShortcut -Force }
$runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
if ($runKey) {
    $runKey.DeleteValue('MoreAccurateIndicator', $false)
    $runKey.Dispose()
}
Write-Output '已移除 More Accurate 的 Skill、MCP 注册和开机启动项。'
Write-Output '保留了本地分数和显示设置。'
