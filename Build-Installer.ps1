param(
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][uri]$Server,
    [string]$KeyPath,
    [string]$UpdatePublicKeyPath,
    [Parameter(Mandatory)][string]$OutDir,
    [switch]$CreateKey,
    [switch]$TestBuild,
    [string]$SignTool,
    [string]$SigningThumbprint,
    [uri]$TimestampServer,
    [string]$ArtifactSigningDlib,
    [string]$ArtifactSigningMetadata
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'desktop\Release-Key.ps1')
if ([bool]$KeyPath -eq [bool]$UpdatePublicKeyPath) {
    throw 'Specify either KeyPath for a local release build or UpdatePublicKeyPath for a public CI build.'
}
if ($CreateKey -and $UpdatePublicKeyPath) { throw 'CreateKey requires KeyPath.' }
if ($Version.Revision -lt 0 -or @($Version.Major, $Version.Minor, $Version.Build, $Version.Revision | Where-Object { $_ -gt 65534 }).Count -gt 0) {
    throw 'Specify four Windows-compatible version parts (e.g. 0.2.0.0).'
}
if (-not $Server.IsAbsoluteUri -or ($Server.Scheme -ne 'https' -and -not ($Server.Scheme -eq 'http' -and $Server.IsLoopback)) -or
    $Server.AbsolutePath -ne '/' -or $Server.UserInfo -or $Server.Query -or $Server.Fragment) { throw 'Specify a HTTPS server root URL, or a loopback HTTP URL for testing.' }
$useArtifactSigning = $ArtifactSigningDlib -or $ArtifactSigningMetadata
if ($SignTool -or $SigningThumbprint -or $TimestampServer -or $useArtifactSigning) {
    if (-not $SignTool) { throw 'Specify SignTool to sign the installer.' }
    $SignTool = (Get-Command $SignTool -CommandType Application -ErrorAction Stop).Source

    if ($useArtifactSigning) {
        if ($SigningThumbprint -or -not $ArtifactSigningDlib -or -not $ArtifactSigningMetadata) {
            throw 'For Azure Artifact Signing, specify the dlib and metadata file together.'
        }
        $ArtifactSigningDlib = (Resolve-Path -LiteralPath $ArtifactSigningDlib).ProviderPath
        $ArtifactSigningMetadata = (Resolve-Path -LiteralPath $ArtifactSigningMetadata).ProviderPath
        $metadata = Get-Content -LiteralPath $ArtifactSigningMetadata -Raw | ConvertFrom-Json
        foreach ($field in 'Endpoint','CodeSigningAccountName','CertificateProfileName') {
            if ($metadata.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($metadata.$field)) {
                throw "Artifact Signing metadata is missing $field."
            }
        }
        if (-not $TimestampServer) { $TimestampServer = [uri]'http://timestamp.acs.microsoft.com' }
        $signingArguments = @('/dlib', $ArtifactSigningDlib, '/dmdf', $ArtifactSigningMetadata)
    } else {
        if ($SigningThumbprint -notmatch '\A[0-9a-fA-F]{40}\z') { throw 'Specify the code-signing certificate thumbprint.' }
        $signingArguments = @('/sha1', $SigningThumbprint)
    }
    if (-not $TimestampServer.IsAbsoluteUri -or $TimestampServer.Scheme -notin @('http','https')) {
        throw 'Specify the RFC 3161 timestamp URL.'
    }
}

function Set-PublisherSignature([string]$File) {
    if (-not $SignTool) { return }
    & $SignTool sign /fd SHA256 /tr $TimestampServer.AbsoluteUri /td SHA256 @signingArguments $File
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $File." }
    & $SignTool verify /pa /tw $File
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $File." }
}
$out = [IO.Path]::GetFullPath($OutDir)
[IO.Directory]::CreateDirectory($out) | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler not found.' }
$framework = Split-Path -Parent $compiler
$stage = Join-Path $out ('build-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$rsa = $null
try {
    if ($UpdatePublicKeyPath) {
        $publicKey = Get-Content -LiteralPath $UpdatePublicKeyPath -Raw | ConvertFrom-Json
        $key = [Security.Cryptography.RSAParameters]::new()
        $key.Modulus = [Convert]::FromBase64String($publicKey.modulus)
        $key.Exponent = [Convert]::FromBase64String($publicKey.exponent)
        if ($key.Modulus.Length -lt 256 -or $key.Exponent.Length -lt 1 -or $key.Exponent.Length -gt 4) {
            throw 'Invalid public update verification key.'
        }
    } else {
        $rsa = Get-DliReleaseKey $KeyPath -Create:$CreateKey
        $key = $rsa.ExportParameters($false)
    }
    $modulus = [Convert]::ToBase64String($key.Modulus)
    $exponent = [Convert]::ToBase64String($key.Exponent)
    $title = -join ([char[]](0xB514,0xC5D8,0xC544,0xC774,0x20,0xC6D0,0xACA9,0xC9C0,0xC6D0))
    $company = $title.Split(' ')[0]
    $settings = Join-Path $stage 'ReleaseSettings.cs'
    @"
using System.Reflection;
[assembly: AssemblyTitle("$title")]
[assembly: AssemblyProduct("$title")]
[assembly: AssemblyCompany("$company")]
[assembly: AssemblyVersion("$Version")]
[assembly: AssemblyFileVersion("$Version")]
internal static class ReleaseSettings
{
    public const string Version = "$Version";
    public const string Server = @"$($Server.AbsoluteUri.Replace('"','""'))";
    public const string UpdateModulus = "$modulus";
    public const string UpdateExponent = "$exponent";
}
"@ | Set-Content -LiteralPath $settings -Encoding UTF8
    $common = @('/nologo','/target:winexe','/optimize+','/codepage:65001','/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
    $icon = Join-Path $PSScriptRoot 'assets\dli.ico'
    $logo = Join-Path $PSScriptRoot 'assets\dli-logo.png'
    $brand = Join-Path $PSScriptRoot 'desktop\Brand.cs'
    $common += "/win32icon:$icon"
    if ($TestBuild) { $common += '/define:DLI_TEST' }
    $product = Join-Path $PSScriptRoot 'desktop\Product.cs'
    $app = Join-Path $stage 'DLI-RemoteSupport.exe'
    $setup = Join-Path $stage 'DLI-RemoteSupport-Setup.exe'
    & $compiler @common '/define:DLI_INSTALLED' "/out:$app" "/r:$framework\System.Net.Http.dll" "/r:$framework\System.Web.Extensions.dll" `
        (Join-Path $PSScriptRoot 'desktop\App.cs') (Join-Path $PSScriptRoot 'desktop\Update.cs') $product $brand $settings `
        (Join-Path $PSScriptRoot 'customer\Customer.cs') (Join-Path $PSScriptRoot 'customer\CustomerFiles.cs')
    if ($LASTEXITCODE -ne 0) { throw "Application compilation failed ($LASTEXITCODE)." }
    Set-PublisherSignature $app
    & $compiler @common "/out:$setup" "/resource:$app,DLI.App" "/resource:$logo,DLI.BrandMark" `
        (Join-Path $PSScriptRoot 'desktop\Setup.cs') (Join-Path $PSScriptRoot 'desktop\InstallWindow.cs') $product $brand $settings
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
    Set-PublisherSignature $setup
    $final = Join-Path $out 'DLI-RemoteSupport-Setup.exe'
    [IO.File]::Copy($setup, $final, $true)
    Write-Output $final
} finally {
    if ($null -ne $rsa) { $rsa.Dispose() }
    foreach ($name in 'ReleaseSettings.cs','DLI-RemoteSupport.exe','DLI-RemoteSupport-Setup.exe') { [IO.File]::Delete((Join-Path $stage $name)) }
    [IO.Directory]::Delete($stage)
}
