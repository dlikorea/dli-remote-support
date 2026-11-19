param(
    [Parameter(Mandatory)][uri]$Server,
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][string]$KeyPath,
    [Parameter(Mandatory)][string]$ReleaseDir,
    [switch]$CreateKey,
    [switch]$NginxProxyManager,
    [string]$SignTool,
    [string]$SigningThumbprint,
    [uri]$TimestampServer,
    [string]$ArtifactSigningDlib,
    [string]$ArtifactSigningMetadata
)

$ErrorActionPreference = 'Stop'
if (-not $Server.IsAbsoluteUri -or $Server.Scheme -ne 'https' -or $Server.IsLoopback -or $Server.AbsolutePath -ne '/' -or
    $Server.UserInfo -or $Server.Query -or $Server.Fragment) { throw 'Specify the public HTTPS root URL of the NAS relay.' }
$release = [IO.Path]::GetFullPath($ReleaseDir)
$nas = Join-Path $release 'nas'
$feed = Join-Path $nas 'feed'
$installer = Join-Path $release ('installers\' + $Version)
$config = Join-Path $nas 'relay.env'
[IO.Directory]::CreateDirectory($nas) | Out-Null
if ([IO.File]::Exists($config)) {
    $existing = [IO.File]::ReadAllLines($config)
    if (('DLI_PUBLIC_ORIGIN=' + $Server.GetLeftPart([UriPartial]::Authority)) -notin $existing -or
        @($existing | Where-Object { $_ -match '\ADLI_OPERATOR_KEY=.{12,128}\z' }).Count -ne 1 -or
        ([bool]($existing -match '\ADLI_PROXY_TOKEN=[a-f0-9]{64}\z') -ne [bool]$NginxProxyManager)) {
        throw 'This directory is configured for another address or has an invalid operator key.'
    }
}

$signing = @{}
foreach ($name in 'SignTool','SigningThumbprint','TimestampServer','ArtifactSigningDlib','ArtifactSigningMetadata') {
    if ($PSBoundParameters.ContainsKey($name)) { $signing[$name] = $PSBoundParameters[$name] }
}
$setup = Join-Path $installer 'DLI-RemoteSupport-Setup.exe'
$manifestPath = Join-Path $feed 'manifest.json'
if ([IO.File]::Exists($manifestPath) -and [version](Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version -ge $Version) {
    throw 'This version is already published. To release a new installer, increment the version.'
}
$publishedSetup = Join-Path $feed "$Version\DLI-RemoteSupport-Setup.exe"
if ([IO.File]::Exists($publishedSetup)) {
    if (-not [IO.File]::Exists($setup)) { throw 'An incomplete release exists. Restore its installer or increment the version.' }
    if ($signing.Count -gt 0) { throw 'An incomplete release exists. Use a new version to build and verify the Windows signatures.' }
} else {
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Version $Version -Server $Server -KeyPath $KeyPath -OutDir $installer -CreateKey:$CreateKey @signing
}
& (Join-Path $PSScriptRoot 'Publish-Update.ps1') -Setup $setup -KeyPath $KeyPath -FeedDir $feed

$compose = if ($NginxProxyManager) { 'compose.npm.yaml' } else { 'compose.yaml' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot $compose) -Destination (Join-Path $nas 'compose.yaml') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server.js') -Destination $nas -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'public') -Destination $nas -Recurse -Force
if (-not [IO.File]::Exists($config)) {
    $key = [byte[]]::new(64)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($key) } finally { $rng.Dispose() }
    $hex = [BitConverter]::ToString($key, 0, 32).Replace('-', '').ToLowerInvariant()
    $proxySecret = [BitConverter]::ToString($key, 32, 32).Replace('-', '').ToLowerInvariant()
    [Array]::Clear($key, 0, $key.Length)
    $configText = 'DLI_PUBLIC_ORIGIN=' + $Server.GetLeftPart([UriPartial]::Authority) + "`nDLI_OPERATOR_KEY=$hex`n"
    if ($NginxProxyManager) { $configText += "DLI_PROXY_TOKEN=$proxySecret`n" }
    $file = [IO.File]::Open($config, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $data = [Text.Encoding]::UTF8.GetBytes($configText)
        $file.Write($data, 0, $data.Length)
        [Array]::Clear($data, 0, $data.Length)
    } finally { $file.Dispose() }
}

Write-Output "NAS project: $nas"
Write-Output "Customer download after deployment: $($Server.AbsoluteUri)download"
