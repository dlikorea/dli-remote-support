param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$KeyPath,
    [Parameter(Mandatory)][string]$FeedDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'desktop\Release-Key.ps1')
$file = Get-Item -LiteralPath $Setup
if ($file.Length -lt 1 -or $file.Length -gt 30MB) { throw 'Invalid installer size.' }
$info = $file.VersionInfo
$version = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart).ToString()
$hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$message = "DLI.RemoteSupport`n$version`n$($file.Length.ToString([Globalization.CultureInfo]::InvariantCulture))`n$hash`n"
$rsa = Get-DliReleaseKey $KeyPath
try {
    $signature = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($message), [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')))
} finally { $rsa.Dispose() }
$root = [IO.Path]::GetFullPath($FeedDir)
$current = Join-Path $root 'manifest.json'
if ([IO.File]::Exists($current)) {
    $previous = Get-Content -LiteralPath $current -Raw | ConvertFrom-Json
    if ([version]$version -lt [version]$previous.version) { throw 'The feed already advertises a newer version.' }
}
$target = Join-Path $root $version
[IO.Directory]::CreateDirectory($target) | Out-Null
$artifact = Join-Path $target 'DLI-RemoteSupport-Setup.exe'
if ([IO.File]::Exists($artifact)) {
    if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'This version was already published with different bytes. Increment the version.' }
} else {
    $staged = Join-Path $target ('setup-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::Copy($file.FullName, $staged, $false)
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Installer changed during publishing.' }
        [IO.File]::Move($staged, $artifact)
    } finally { [IO.File]::Delete($staged) }
}
$manifest = @{ version = $version; size = $file.Length; sha256 = $hash; signature = $signature } | ConvertTo-Json -Compress
$temporary = Join-Path $root ('manifest-' + [guid]::NewGuid().ToString('N') + '.tmp')
$backup = "$temporary.bak"
try {
    [IO.File]::WriteAllText($temporary, $manifest, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($current)) { [IO.File]::Replace($temporary, $current, $backup) }
    else { [IO.File]::Move($temporary, $current) }
} finally {
    [IO.File]::Delete($temporary)
    [IO.File]::Delete($backup)
}
Write-Output $current
