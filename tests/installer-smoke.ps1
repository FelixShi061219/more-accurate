# Development test: Windows PowerShell, no CLI on PATH, isolated Codex home.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$testHome = Join-Path ([IO.Path]::GetTempPath()) ('more-accurate-install-test-' + [Guid]::NewGuid().ToString('N'))
$previousPath = $env:PATH
$previousCodexHome = $env:CODEX_HOME
try {
    $env:PATH = Join-Path $env:WINDIR 'System32'
    $env:CODEX_HOME = $testHome
    New-Item -ItemType Directory -Path $testHome -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $testHome 'config.toml'), "# preserved-install-fixture`n[otel]`n# BEGIN MORE ACCURATE LOCAL TELEMETRY`nexporter = 'none'`n# END MORE ACCURATE LOCAL TELEMETRY`n")
    if (Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue) {
        throw 'Test requires a PATH without codex.exe.'
    }
    $testDesktop = Join-Path $testHome 'desktop'
    & (Join-Path $root 'setup.ps1') -NoLaunch -SkipBrowserBridge -DesktopDirectory $testDesktop
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $testDesktop 'More Accurate.lnk'))
    if ($link.TargetPath -ne (Join-Path $root 'app\MoreAccurateIndicator.exe')) { throw 'Desktop shortcut target mismatch.' }
    if ($LASTEXITCODE -ne 0) { throw 'Installation failed.' }
    $configText = [IO.File]::ReadAllText((Join-Path $testHome 'config.toml'))
    if ($configText.Contains('# BEGIN MORE ACCURATE LOCAL TELEMETRY') -or -not $configText.Contains('# preserved-install-fixture')) { throw 'Old telemetry cleanup or config preservation failed.' }
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $root 'codex-skill\SKILL.md')).Hash
    $installedHash = (Get-FileHash -LiteralPath (Join-Path $testHome 'skills\more-accurate\SKILL.md')).Hash
    if ($sourceHash -ne $installedHash) { throw 'Installed skill differs from source.' }
    . (Join-Path $root 'app\find-codex.ps1')
    $cli = Resolve-CodexCli
    $registrationText = & $cli mcp get more-accurate --json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read back MCP registration.' }
    $registration = $registrationText | ConvertFrom-Json
    if ($registration.transport.command -ne (Join-Path $root 'app\MoreAccurateIndicator.exe')) {
        throw 'Incorrect MCP executable.'
    }
    if ($registration.transport.args -notcontains '--mcp') { throw 'Missing MCP argument.' }
    Write-Output 'PASS: no CLI on PATH; bundled CLI detected; skill installed; MCP registered and verified.'
    Write-Output ('Isolated test data: ' + $testHome)
}
finally {
    $env:PATH = $previousPath
    $env:CODEX_HOME = $previousCodexHome
}
