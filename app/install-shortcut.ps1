param([string]$DesktopDirectory = [Environment]::GetFolderPath('Desktop'))
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'MoreAccurateIndicator.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '找不到浮窗程序，无法创建桌面快捷方式。' }
New-Item -ItemType Directory -Path $DesktopDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $DesktopDirectory 'More Accurate.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = 'More Accurate'
$shortcut.IconLocation = $exe + ',0'
$shortcut.Save()
$oldPath = Join-Path $DesktopDirectory 'More Accurate 需求理解度.lnk'
if (Test-Path -LiteralPath $oldPath) {
    $old = $shell.CreateShortcut($oldPath)
    if ($old.TargetPath -eq $exe) { Remove-Item -LiteralPath $oldPath }
}
Write-Output '已创建桌面启动快捷方式。'
