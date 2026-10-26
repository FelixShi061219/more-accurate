param([string]$CodexHome = $env:CODEX_HOME)
$ErrorActionPreference = 'Stop'
if (-not $CodexHome) { $CodexHome = Join-Path $env:USERPROFILE '.codex' }
$config = Join-Path $CodexHome 'config.toml'
New-Item -ItemType Directory -Force -Path $CodexHome | Out-Null
$text = if (Test-Path -LiteralPath $config) { [IO.File]::ReadAllText($config) } else { '' }
$section = [regex]::Match($text, '(?ms)^\[otel\][^\r\n]*\r?\n.*?(?=^\[|\z)')
$begin = '# BEGIN MORE ACCURATE LOCAL TELEMETRY'
$end = '# END MORE ACCURATE LOCAL TELEMETRY'
if ($text.Contains($begin)) { Write-Output '本机遥测已配置；重启 Codex 后生效。'; return }
if ([regex]::IsMatch($text, '(?m)^\[otel\.(exporter|exporters)[.\]]|^\s*otel\s*[.=]')) {
    Write-Warning '检测到已有遥测导出器，已保留。浮窗仍可读取本轮模型和生成状态；请求延迟显示 None，直到将日志转发到本机 http://127.0.0.1:4319/v1/logs。'; return
}
if ($section.Success -and [regex]::IsMatch($section.Value, '(?m)^\s*exporter\s*=')) {
    Write-Warning '检测到已有遥测导出器，已保留。浮窗仍可读取本轮模型和生成状态；请求延迟显示 None，直到将日志转发到本机 http://127.0.0.1:4319/v1/logs。'; return
}
$block = "$begin`r`nexporter = { otlp-http = { endpoint = `"http://127.0.0.1:4319/v1/logs`", protocol = `"binary`" } }`r`n$end`r`n"
if ($section.Success) {
    # A privacy-sensitive existing true value is left intact; do not silently repurpose it.
    if ([regex]::IsMatch($section.Value, '(?m)^\s*log_user_prompt\s*=\s*true')) {
        Write-Warning '已有 log_user_prompt=true。请先关闭提示词遥测，再启用本机连接；当前配置未改动。'; return
    }
    if (-not [regex]::IsMatch($section.Value, '(?m)^\s*log_user_prompt\s*=')) { $block = $block.Replace("$end", "log_user_prompt = false`r`n$end") }
    $updated = $text.Insert($section.Index + $section.Length, $block)
} else {
    $block = $block.Replace("$end", "log_user_prompt = false`r`n$end")
    $updated = $text.TrimEnd() + "`r`n`r`n[otel]`r`n" + $block
}
if (Test-Path -LiteralPath $config) {
    $backup = Join-Path $CodexHome ('config.before-more-accurate-telemetry.' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.toml')
    Copy-Item -LiteralPath $config -Destination $backup
}
$temporary = $config + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
[IO.File]::WriteAllText($temporary, $updated, (New-Object Text.UTF8Encoding($false)))
if (Test-Path -LiteralPath $config) { [IO.File]::Replace($temporary, $config, [NullString]::Value) } else { [IO.File]::Move($temporary, $config) }
Write-Output '已启用 Codex 本机遥测（127.0.0.1:4319），提示词内容关闭。请重启 Codex 后使用。'
