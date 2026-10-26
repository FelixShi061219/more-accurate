Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ErrorActionPreference = 'Stop'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'MoreAccurateIndicator'
$statePath = Join-Path $dataDirectory 'state.json'
$preferencesPath = Join-Path $dataDirectory 'preferences.json'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'More Accurate Indicator.lnk'
[System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null

$preferences = @{ topmost = $false; launchAtLogin = $false; rememberPosition = $false; x = 120; y = 120 }
if (Test-Path -LiteralPath $preferencesPath) {
    try {
        $saved = Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json
        foreach ($key in @('topmost', 'launchAtLogin', 'rememberPosition')) { $preferences[$key] = [bool]$saved.$key }
        if ($preferences.rememberPosition) { $preferences.x = [int]$saved.x; $preferences.y = [int]$saved.y }
    } catch { }
}

$script:understanding = 0
$script:reliability = 0
$script:activeMode = 'understanding'
$script:note = 'Waiting for Codex'
$script:flow = 0
$script:dragOffset = [System.Drawing.Point]::Empty
$script:preferences = $preferences

function Save-Preferences {
    $script:preferences.x = $form.Left
    $script:preferences.y = $form.Top
    $script:preferences | ConvertTo-Json | Set-Content -LiteralPath $preferencesPath -Encoding utf8
}

function Set-LoginShortcut([bool]$enabled) {
    if ($enabled) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($startupShortcut)
        $shortcut.TargetPath = (Get-Command powershell.exe).Source
        $shortcut.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        $shortcut.WorkingDirectory = Split-Path -Parent $PSCommandPath
        $shortcut.Save()
    } elseif (Test-Path -LiteralPath $startupShortcut) {
        Remove-Item -LiteralPath $startupShortcut -Force
    }
}

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.ShowInTaskbar = $false
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.ClientSize = New-Object System.Drawing.Size(390, 62)
$form.BackColor = [System.Drawing.Color]::Fuchsia
$form.TransparencyKey = [System.Drawing.Color]::Fuchsia
$form.TopMost = $preferences.topmost
$form.Opacity = 1
$form.Location = New-Object System.Drawing.Point([int]$preferences.x, [int]$preferences.y)
$form.DoubleBuffered = $true
$form.Text = 'Confidence indicator'

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$modeUnderstanding = $menu.Items.Add('显示：需求理解度')
$modeReliability = $menu.Items.Add('显示：答案可靠度')
$menu.Items.Add('-') | Out-Null
$topmostItem = $menu.Items.Add('置顶')
$topmostItem.CheckOnClick = $true
$topmostItem.Checked = $preferences.topmost
$startupItem = $menu.Items.Add('开机启动')
$startupItem.CheckOnClick = $true
$startupItem.Checked = $preferences.launchAtLogin
$rememberItem = $menu.Items.Add('记住位置')
$rememberItem.CheckOnClick = $true
$rememberItem.Checked = $preferences.rememberPosition
$menu.Items.Add('-') | Out-Null
$exitItem = $menu.Items.Add('退出')
$form.ContextMenuStrip = $menu

$form.Add_MouseDown({
    if ($_.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        $script:dragOffset = [System.Windows.Forms.Cursor]::Position - $form.Location
    }
})
$form.Add_MouseMove({
    if ([System.Windows.Forms.Control]::MouseButtons -eq [System.Windows.Forms.MouseButtons]::Left) {
        $form.Location = [System.Windows.Forms.Cursor]::Position - $script:dragOffset
        if ($script:preferences.rememberPosition) { Save-Preferences }
    }
})
$form.Add_MouseUp({ if ($script:preferences.rememberPosition) { Save-Preferences } })
$modeUnderstanding.Add_Click({ $script:activeMode = 'understanding'; $form.Invalidate() })
$modeReliability.Add_Click({ $script:activeMode = 'reliability'; $form.Invalidate() })
$topmostItem.Add_Click({ $form.TopMost = $topmostItem.Checked; $script:preferences.topmost = $topmostItem.Checked; Save-Preferences })
$startupItem.Add_Click({ $script:preferences.launchAtLogin = $startupItem.Checked; Set-LoginShortcut $startupItem.Checked; Save-Preferences })
$rememberItem.Add_Click({ $script:preferences.rememberPosition = $rememberItem.Checked; Save-Preferences })
$exitItem.Add_Click({ $form.Close() })

$form.Add_Paint({
    $graphics = $_.Graphics
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $graphics.Clear([System.Drawing.Color]::Fuchsia)

    $bounds = New-Object System.Drawing.Rectangle(12, 10, 366, 42)
    $radius = 21
    $trackPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $trackPath.AddArc($bounds.Left, $bounds.Top, $radius * 2, $radius * 2, 180, 90)
    $trackPath.AddArc($bounds.Right - ($radius * 2), $bounds.Top, $radius * 2, $radius * 2, 270, 90)
    $trackPath.AddArc($bounds.Right - ($radius * 2), $bounds.Bottom - ($radius * 2), $radius * 2, $radius * 2, 0, 90)
    $trackPath.AddArc($bounds.Left, $bounds.Bottom - ($radius * 2), $radius * 2, $radius * 2, 90, 90)
    $trackPath.CloseFigure()

    $selectedScore = if ($script:activeMode -eq 'understanding') { $script:understanding } else { $script:reliability }
    $fillWidth = [Math]::Max(40, [int](($bounds.Width - 4) * $selectedScore / 100))
    $fillBounds = New-Object System.Drawing.Rectangle(($bounds.Left + 2), ($bounds.Top + 2), [Math]::Min($fillWidth, $bounds.Width - 4), ($bounds.Height - 4))
    $fillPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fillRadius = [Math]::Min(19, [int]($fillBounds.Height / 2))
    $fillPath.AddArc($fillBounds.Left, $fillBounds.Top, $fillRadius * 2, $fillRadius * 2, 180, 90)
    $fillPath.AddArc($fillBounds.Right - ($fillRadius * 2), $fillBounds.Top, $fillRadius * 2, $fillRadius * 2, 270, 90)
    $fillPath.AddArc($fillBounds.Right - ($fillRadius * 2), $fillBounds.Bottom - ($fillRadius * 2), $fillRadius * 2, $fillRadius * 2, 0, 90)
    $fillPath.AddArc($fillBounds.Left, $fillBounds.Bottom - ($fillRadius * 2), $fillRadius * 2, $fillRadius * 2, 90, 90)
    $fillPath.CloseFigure()

    $trackBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(238, 250, 250, 252))
    $graphics.FillPath($trackBrush, $trackPath)
    $graphics.SetClip($fillPath)
    if ($selectedScore -ge 90) {
        $colors = @([System.Drawing.Color]::FromArgb(255, 244, 65), [System.Drawing.Color]::FromArgb(72, 211, 117), [System.Drawing.Color]::FromArgb(54, 157, 246), [System.Drawing.Color]::FromArgb(190, 111, 255), [System.Drawing.Color]::FromArgb(255, 78, 124))
    } elseif ($selectedScore -ge 75) {
        $colors = @([System.Drawing.Color]::FromArgb(68, 207, 126), [System.Drawing.Color]::FromArgb(88, 225, 162), [System.Drawing.Color]::FromArgb(57, 195, 130))
    } elseif ($selectedScore -ge 50) {
        $colors = @([System.Drawing.Color]::FromArgb(241, 222, 100), [System.Drawing.Color]::FromArgb(255, 239, 137), [System.Drawing.Color]::FromArgb(241, 218, 92))
    } elseif ($selectedScore -ge 25) {
        $colors = @([System.Drawing.Color]::FromArgb(255, 162, 48), [System.Drawing.Color]::FromArgb(255, 191, 71), [System.Drawing.Color]::FromArgb(250, 146, 40))
    } else {
        $colors = @([System.Drawing.Color]::FromArgb(250, 67, 79), [System.Drawing.Color]::FromArgb(255, 104, 99), [System.Drawing.Color]::FromArgb(238, 52, 86))
    }
    $stops = [System.Drawing.ColorBlend]::new()
    $stops.Colors = $colors
    $positions = New-Object 'System.Single[]' $colors.Length
    for ($index = 0; $index -lt $positions.Length; $index++) { $positions[$index] = [single]($index / [Math]::Max(1, ($positions.Length - 1))) }
    $stops.Positions = $positions
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($fillBounds, [System.Drawing.Color]::White, [System.Drawing.Color]::Black, [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $brush.InterpolationColors = $stops
    $brush.TranslateTransform(-$script:flow, 0)
    $graphics.FillPath($brush, $fillPath)
    $graphics.ResetClip()

    $label = if ($script:activeMode -eq 'understanding') { '理解' } else { '答案' }
    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10, [System.Drawing.FontStyle]::Bold)
    $smallFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 8, [System.Drawing.FontStyle]::Regular)
    $scoreFont = New-Object System.Drawing.Font('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
    $labelBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(35, 39, 45))
    $subtleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(105, 112, 120))
    $graphics.DrawString($label, $font, $labelBrush, 26, 17)
    $otherLabel = if ($script:activeMode -eq 'understanding') { '答案' } else { '理解' }
    $graphics.DrawString($otherLabel, $smallFont, $subtleBrush, 74, 22)
    $graphics.DrawString("$selectedScore%", $scoreFont, $labelBrush, 320, 15)
    $graphics.DrawRectangle([System.Drawing.Pens]::LightGray, 65, 19, 1, 24)

    foreach ($object in @($trackPath, $fillPath, $trackBrush, $brush, $font, $smallFont, $scoreFont, $labelBrush, $subtleBrush, $stops)) { if ($object -is [System.IDisposable]) { $object.Dispose() } }
})

$poll = New-Object System.Windows.Forms.Timer
$poll.Interval = 180
$poll.Add_Tick({
    if (Test-Path -LiteralPath $statePath) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            $script:understanding = [Math]::Max(0, [Math]::Min(100, [int]$state.understanding))
            $script:reliability = [Math]::Max(0, [Math]::Min(100, [int]$state.reliability))
            if ($state.activeMode -in @('understanding', 'reliability')) { $script:activeMode = [string]$state.activeMode }
            $script:note = [string]$state.note
            $form.AccessibleDescription = $script:note
        } catch { }
    }
    $script:flow = ($script:flow + 1.1) % 72
    $form.Invalidate()
})
$form.Add_Shown({ $poll.Start() })
$form.Add_FormClosed({
    $poll.Stop()
    if ($script:preferences.rememberPosition) { Save-Preferences }
})

[System.Windows.Forms.Application]::Run($form)
