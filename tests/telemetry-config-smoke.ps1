$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('more-accurate-config-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$config = Join-Path $testRoot 'config.toml'
try {
    $baseline = 'model = "test-model"' + "`r`n" + '[profiles.test]' + "`r`n" + 'model = "other-model"' + "`r`n"
    [IO.File]::WriteAllText($config, $baseline)
    & (Join-Path $root 'app\enable-telemetry.ps1') -CodexHome $testRoot
    $installed = [IO.File]::ReadAllText($config)
    if (-not $installed.StartsWith($baseline.TrimEnd())) { throw 'Unrelated configuration changed.' }
    if ($installed -notmatch 'http://127\.0\.0\.1:4319/v1/logs' -or $installed -notmatch 'log_user_prompt = false') { throw 'Local exporter/privacy configuration missing.' }
    & (Join-Path $root 'app\enable-telemetry.ps1') -CodexHome $testRoot
    if ([IO.File]::ReadAllText($config) -cne $installed) { throw 'Telemetry setup is not idempotent.' }
    $existing = $baseline + "[otel]`r`nexporter = `"none`"`r`n"
    [IO.File]::WriteAllText($config, $existing)
    & (Join-Path $root 'app\enable-telemetry.ps1') -CodexHome $testRoot -WarningAction SilentlyContinue
    if ([IO.File]::ReadAllText($config) -cne $existing) { throw 'Existing exporter was overwritten.' }
    $existing = "[otel]`r`nenvironment = `"test`"`r`n[profiles.other]`r`nmodel = `"other`"`r`n"
    [IO.File]::WriteAllText($config, $existing)
    & (Join-Path $root 'app\enable-telemetry.ps1') -CodexHome $testRoot
    $installed = [IO.File]::ReadAllText($config)
    if ($installed -notmatch '(?s)\[otel\].*127\.0\.0\.1.*\[profiles.other\]') { throw 'Exporter inserted into wrong TOML section.' }
    $sensitive = "[otel]`r`nlog_user_prompt = true`r`n"
    [IO.File]::WriteAllText($config, $sensitive)
    & (Join-Path $root 'app\enable-telemetry.ps1') -CodexHome $testRoot -WarningAction SilentlyContinue
    if ([IO.File]::ReadAllText($config) -cne $sensitive) { throw 'Privacy conflict must preserve existing configuration.' }
    Write-Output 'PASS: isolated config installation, preservation, privacy settings, TOML section placement, idempotency and existing-exporter conflict.'
} finally {
    # Only exact files created inside this unique, resolved temporary test directory.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    foreach ($file in Get-ChildItem -LiteralPath $resolved -File) { Remove-Item -LiteralPath $file.FullName }
    Remove-Item -LiteralPath $resolved
}
