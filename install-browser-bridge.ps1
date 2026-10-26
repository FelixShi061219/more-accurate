param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$hostName = 'com.moreaccurate.indicator'
$nativeManifest = Join-Path $root 'app\data\native-host.json'
$keys = @(('Software\Google\Chrome\NativeMessagingHosts\' + $hostName), ('Software\Microsoft\Edge\NativeMessagingHosts\' + $hostName))
if ($Remove) {
    foreach ($keyPath in $keys) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
        if ($key) {
            $matches = $key.GetValue('') -eq $nativeManifest
            $key.Dispose()
            if ($matches) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($keyPath, $false) }
        }
    }
    Write-Output 'Removed matching browser native host registrations.'
    return
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'browser-extension\manifest.json') -Raw | ConvertFrom-Json
$sha = [Security.Cryptography.SHA256]::Create()
try { $digest = $sha.ComputeHash([Convert]::FromBase64String($manifest.key)) } finally { $sha.Dispose() }
$extensionId = -join ($digest[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
$exe = Join-Path $root 'app\MoreAccurateIndicator.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build or install the application first.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $nativeManifest) -Force | Out-Null
$config = @{ name=$hostName; description='More Accurate local indicator bridge'; path=$exe; type='stdio'; allowed_origins=@('chrome-extension://' + $extensionId + '/') }
[IO.File]::WriteAllText($nativeManifest,($config | ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
foreach ($keyPath in $keys) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyPath)
    try { $key.SetValue('', $nativeManifest) } finally { $key.Dispose() }
}
Write-Output ('Extension ID: ' + $extensionId)
Write-Output ('Load unpacked in Edge / Chrome: ' + (Join-Path $root 'browser-extension'))
Write-Output 'Enable developer mode on edge://extensions or chrome://extensions, load the folder, then enable linking in the extension popup and refresh the AI page.'
