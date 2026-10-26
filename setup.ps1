param([switch]$NoLaunch, [switch]$SkipBrowserBridge, [string]$DesktopDirectory = [Environment]::GetFolderPath('Desktop'))
$ErrorActionPreference = 'Stop'
if (-not $env:CODEX_HOME) { $env:CODEX_HOME = Join-Path $env:USERPROFILE '.codex' }
$root = Split-Path -Parent $PSCommandPath
$skillTarget = Join-Path $env:CODEX_HOME 'skills\more-accurate'
$exe = Join-Path $root 'app\MoreAccurateIndicator.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'app\build.ps1') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '编译失败，安装已停止。' }
}
. (Join-Path $root 'app\find-codex.ps1')
$codex = Resolve-CodexCli
if ($codex) {
    Write-Output ('已找到 Codex CLI：' + $codex)
    $config = Join-Path $env:CODEX_HOME 'config.toml'
    $backup = Join-Path $env:CODEX_HOME ('config.before-more-accurate.' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.toml')
    if (Test-Path -LiteralPath $config) {
        Copy-Item -LiteralPath $config -Destination $backup
        $text = [IO.File]::ReadAllText($config)
        $pattern = '(?ms)^# BEGIN MORE ACCURATE LOCAL TELEMETRY\r?\n.*?^# END MORE ACCURATE LOCAL TELEMETRY\r?\n?'
        if ([regex]::IsMatch($text, $pattern)) {
            [IO.File]::WriteAllText($config, [regex]::Replace($text, $pattern, ''), (New-Object Text.UTF8Encoding($false)))
            Write-Output '已移除本项目旧版遥测块。'
        }
    }
    New-Item -ItemType Directory -Force -Path $skillTarget | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'codex-skill\SKILL.md') -Destination (Join-Path $skillTarget 'SKILL.md') -Force
    & $codex mcp add more-accurate -- $exe --mcp
    if ($LASTEXITCODE -ne 0) { throw 'MCP 注册失败，请查看上方错误。' }
    Write-Output '中文 Skill 已安装，MCP 已注册。'
} else {
    Write-Output '未检测到 Codex；继续安装浮窗和浏览器桥接。其他 AI 不需要 Codex。'
}
& (Join-Path $root 'app\install-shortcut.ps1') -DesktopDirectory $DesktopDirectory
if (-not $SkipBrowserBridge) { & (Join-Path $root 'install-browser-bridge.ps1') }
if (-not $NoLaunch) {
    Get-CimInstance Win32_Process -Filter "Name='MoreAccurateIndicator.exe'" | Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -notmatch '--mcp' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Process -FilePath $exe -WindowStyle Hidden
    Write-Output '透明浮条已启动。'
}
Write-Output '请重启 Codex，然后输入：使用 $more-accurate，先帮我明确这个任务。'
Write-Output '请保留本项目目录；联动服务和开机启动需要其中的程序文件。'
