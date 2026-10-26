function Resolve-CodexCli {
    param([switch]$SkipPath)

    if (-not $SkipPath) {
        foreach ($commandName in @('codex.exe', 'codex.cmd')) {
            $command = Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
                return $command.Source
            }
        }
    }

    # Codex Desktop bundles its CLI in a versioned bin directory. Normal
    # Windows shells do not necessarily inherit the app's additional PATH.
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $binRoot = Join-Path $localData 'OpenAI\Codex\bin'
    $direct = Join-Path $binRoot 'codex.exe'
    if (Test-Path -LiteralPath $direct -PathType Leaf) { return $direct }
    if (Test-Path -LiteralPath $binRoot -PathType Container) {
        $versions = Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        foreach ($version in $versions) {
            $candidate = Join-Path $version.FullName 'codex.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    return $null
}
