param(
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][uri]$Server,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$MakeAppx,
    [string]$IdentityPath,
    [switch]$ValidationBuild
)
$ErrorActionPreference = 'Stop'
if ($Version.Revision -ne 0 -or @($Version.Major, $Version.Minor, $Version.Build | Where-Object { $_ -lt 0 -or $_ -gt 65535 }).Count) {
    throw 'Specify four version parts with a zero revision for Microsoft Store (for example 1.3.0.0).'
}
if (-not $Server.IsAbsoluteUri -or ($Server.Scheme -ne 'https' -and -not ($ValidationBuild -and $Server.IsLoopback -and $Server.Scheme -eq 'http')) -or
    $Server.AbsolutePath -ne '/' -or $Server.UserInfo -or $Server.Query -or $Server.Fragment) {
    throw 'Specify an HTTPS server root. Loopback HTTP is allowed only for validation.'
}
if ($ValidationBuild) {
    if ($IdentityPath) { throw 'Validation builds use a separate local identity, not the Store identity.' }
    $identity = [pscustomobject]@{ identityName='DLI.RemoteSupport.Validation'; publisher='CN=DLI Local Validation'; publisherDisplayName='디엘아이'; storeProductId=$null }
} else {
    if (-not $IdentityPath) { throw 'Copy the assigned package identity from Partner Center into IdentityPath before building for submission.' }
    $identity = Get-Content -LiteralPath $IdentityPath -Raw | ConvertFrom-Json
    foreach ($name in 'identityName','publisher','publisherDisplayName','storeProductId') {
        if ($identity.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($identity.$name) -or $identity.$name -match 'COPY_|[<>\r\n]') {
            throw "Missing or placeholder Store identity field: $name."
        }
    }
    if ($identity.identityName -notmatch '\A[A-Za-z0-9.-]{3,50}\z' -or $identity.publisher -notmatch '\ACN=.+\z' -or
        $identity.storeProductId -notmatch '\A[A-Za-z0-9]{12}\z') { throw 'Invalid Partner Center package identity or Store product ID.' }
}
$makeAppxPath = (Get-Command $MakeAppx -CommandType Application -ErrorAction Stop).Source
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler not found.' }
$framework = Split-Path -Parent $compiler
$out = [IO.Path]::GetFullPath($OutDir)
[IO.Directory]::CreateDirectory($out) | Out-Null
$stage = Join-Path $out ('msix-build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$assets = Join-Path $payload 'Assets'
[IO.Directory]::CreateDirectory($assets) | Out-Null
$resultPath = Join-Path $out ('DLI-RemoteSupport-' + $Version + $(if ($ValidationBuild) { '-validation' }) + '-x64.msix')
try {
    $serverLiteral = $Server.AbsoluteUri.Replace('"','""')
    $settings = Join-Path $stage 'ReleaseSettings.cs'
    @"
using System.Reflection;
[assembly: AssemblyTitle("디엘아이 원격지원")]
[assembly: AssemblyProduct("디엘아이 원격지원")]
[assembly: AssemblyCompany("디엘아이")]
[assembly: AssemblyVersion("$Version")]
[assembly: AssemblyFileVersion("$Version")]
internal static class ReleaseSettings
{
    public const string Version = "$Version";
    public const string Server = @"$serverLiteral";
}
"@ | Set-Content -LiteralPath $settings -Encoding UTF8
    $executable = Join-Path $payload 'DLI-RemoteSupport.exe'
    $arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/define:DLI_INSTALLED,DLI_STORE',
        "/out:$executable", "/win32icon:$(Join-Path $PSScriptRoot 'assets\dli.ico')",
        "/win32manifest:$(Join-Path $PSScriptRoot 'store\app.manifest')",
        '/r:System.Drawing.dll','/r:System.Windows.Forms.dll', "/r:$framework\System.Net.Http.dll", "/r:$framework\System.Web.Extensions.dll")
    & $compiler @arguments (Join-Path $PSScriptRoot 'desktop\App.cs') (Join-Path $PSScriptRoot 'desktop\Product.cs') `
        (Join-Path $PSScriptRoot 'desktop\Brand.cs') (Join-Path $PSScriptRoot 'customer\Customer.cs') `
        (Join-Path $PSScriptRoot 'customer\CustomerFiles.cs') $settings
    if ($LASTEXITCODE -ne 0) { throw 'Store application compilation failed.' }
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup></configuration>
'@ | Set-Content -LiteralPath ($executable + '.config') -Encoding UTF8

    Add-Type -AssemblyName System.Drawing
    $mark = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'assets\dli-logo.png'))
    try {
        foreach ($asset in @(
            @{ Name='StoreLogo.png'; Width=50; Height=50 },
            @{ Name='Square44x44Logo.png'; Width=44; Height=44 },
            @{ Name='Square150x150Logo.png'; Width=150; Height=150 },
            @{ Name='Wide310x150Logo.png'; Width=310; Height=150 },
            @{ Name='StoreListingLogo.png'; Width=300; Height=300 }
        )) {
            $bitmap = [Drawing.Bitmap]::new($asset.Width, $asset.Height)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::White)
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $scale = [Math]::Min(($asset.Width * 0.82) / $mark.Width, ($asset.Height * 0.82) / $mark.Height)
                $width = [int][Math]::Round($mark.Width * $scale)
                $height = [int][Math]::Round($mark.Height * $scale)
                $graphics.DrawImage($mark, [int](($asset.Width-$width)/2), [int](($asset.Height-$height)/2), $width, $height)
                $bitmap.Save((Join-Path $assets $asset.Name), [Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
    } finally { $mark.Dispose() }
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'store\AppxManifest.xml') -Raw
    foreach ($replacement in @{
        '__IDENTITY_NAME__'=$identity.identityName; '__PUBLISHER__'=$identity.publisher;
        '__PUBLISHER_DISPLAY_NAME__'=$identity.publisherDisplayName; '__VERSION__'=$Version.ToString()
    }.GetEnumerator()) { $manifest = $manifest.Replace($replacement.Key, [Security.SecurityElement]::Escape($replacement.Value)) }
    $manifest | Set-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Encoding UTF8
    & $makeAppxPath pack /d $payload /p $resultPath /o
    if ($LASTEXITCODE -ne 0) { throw 'MSIX validation or packaging failed.' }
    $reviewAssets = Join-Path $out 'StoreAssets'
    [IO.Directory]::CreateDirectory($reviewAssets) | Out-Null
    [IO.File]::Copy((Join-Path $assets 'StoreListingLogo.png'), (Join-Path $reviewAssets 'StoreListingLogo.png'), $true)
    [ordered]@{
        version=$Version.ToString(); identityName=$identity.identityName; publisher=$identity.publisher;
        storeProductId=$identity.storeProductId; validationOnly=[bool]$ValidationBuild;
        file=[IO.Path]::GetFileName($resultPath); sha256=(Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash.ToLowerInvariant();
        updates='Microsoft Store'; relayServer=$Server.AbsoluteUri
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'package-info.json') -Encoding UTF8
    Write-Output $resultPath
} finally {
    $stageFull = [IO.Path]::GetFullPath($stage)
    if (-not $stageFull.StartsWith($out.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($stageFull) -notmatch '\Amsix-build-[0-9a-f]{32}\z') { throw 'Refusing to clean an unexpected build path.' }
    if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
}
