param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
$packagePath = (Resolve-Path -LiteralPath $Package).ProviderPath
$inspectionBase = [IO.Path]::GetDirectoryName($packagePath)
$inspectionRoot = Join-Path $inspectionBase ('dli-msix-inspect-' + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $inspectionRoot)
try {
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $inspectionRoot 'AppxManifest.xml') -Raw
    if ($manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Expected the x64 package.' }
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('f','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $ns.AddNamespace('r','http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    $capabilities = $manifest.SelectNodes('/f:Package/f:Capabilities/r:Capability', $ns)
    if ($capabilities.Count -ne 1 -or $capabilities[0].Name -ne 'runFullTrust') { throw 'Unexpected restricted capabilities.' }
    if (@($manifest.Package.Applications.Application).Count -ne 1) { throw 'Expected one customer application entry.' }
    if ($manifest.Package.Applications.Application.EntryPoint -ne 'Windows.FullTrustApplication') { throw 'Desktop full-trust entry point missing.' }
    foreach ($entry in Get-ChildItem -LiteralPath $inspectionRoot -Recurse -File) {
        if ($entry.Name -match '\.(dpapi|pfx|p12|pem|key|env)$' -or $entry.Name -eq 'DLI-RemoteSupport-Setup.exe') {
            throw "Unexpected private key, configuration, or secondary installer in MSIX: $($entry.Name)."
        }
    }
    $probe = @'
param([string]$Root)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFile((Join-Path $Root 'DLI-RemoteSupport.exe'))
foreach ($blockedType in 'UpdateClient','StartupWindow','Setup') {
    if ($assembly.GetType($blockedType)) { throw "Store payload must not include the NAS installer update path: $blockedType." }
}
$customer=[Activator]::CreateInstance($assembly.GetType('CustomerWindow'), [object[]]@('https://remote.dlicompany.com/', $true))
try {
    if ($customer.Text -ne '디엘아이 원격지원') { throw 'Customer title mismatch.' }
} finally { $customer.Dispose() }
$request=[Activator]::CreateInstance($assembly.GetType('FileRequest'))
$files=$assembly.GetType('CustomerFiles')
$drives=$files.GetMethod('ListDrives').Invoke($null,[object[]]@($request,[Threading.CancellationToken]::None))
$systemDrive=[IO.Path]::GetPathRoot($env:WINDIR).TrimEnd('\')
if ($systemDrive -notin $drives.drives) { throw 'System drive is missing from the Store payload file browser.' }
$fixtureName='dli-msix-fixture-' + [guid]::NewGuid().ToString('N') + '.txt'
$fixturePath=Join-Path $Root $fixtureName
[IO.File]::WriteAllText($fixturePath,'DLI MSIX payload file access verification',[Text.Encoding]::UTF8)
$request.root=[IO.Path]::GetPathRoot($fixturePath).TrimEnd('\')
$request.segments=$fixturePath.Substring(3).Split('\')
$stream=$files.GetMethod('Open').Invoke($null,[object[]]@($request))
try {
    $reader=[IO.StreamReader]::new($stream)
    if ($reader.ReadToEnd() -ne 'DLI MSIX payload file access verification') { throw 'Store payload file contents mismatch.' }
} finally { $stream.Dispose() }
[IO.File]::SetAttributes($fixturePath,[IO.FileAttributes]::Hidden)
$hiddenRejected=$false
try { $files.GetMethod('Open').Invoke($null,[object[]]@($request)) | Out-Null } catch { $hiddenRejected=$true }
if (-not $hiddenRejected) { throw 'The volume root exception must not permit hidden child files.' }
$request.segments=[string[]]@('..',$fixtureName)
$rejected=$false
try { $files.GetMethod('Open').Invoke($null,[object[]]@($request)) | Out-Null } catch { $rejected=$true }
if (-not $rejected) { throw 'Path traversal must be rejected.' }
'PASS compiled Store payload: customer window, available drives, file read, hidden child and unsafe path rejection, no NAS updater or nested installer.'
'These are unpacked-payload checks. Store installation and two-PC remote control require the registered Store package.'
'@
    $frameworkHost = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $rootBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($inspectionRoot))
    $probeCommand = "& {`n" + $probe + "`n} -Root ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$rootBase64')))"
    & $frameworkHost -NoProfile -STA -OutputFormat Text -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probeCommand)))
    if ($LASTEXITCODE -ne 0) { throw 'Store payload verification failed.' }
    'PASS MSIX identity format, full-trust declaration, and package content checks.'
} finally {
    $temporaryBase = [IO.Path]::GetFullPath($inspectionBase).TrimEnd('\') + '\'
    $inspectionFull = [IO.Path]::GetFullPath($inspectionRoot)
    if (-not $inspectionFull.StartsWith($temporaryBase,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($inspectionFull) -notmatch '\Adli-msix-inspect-[0-9a-f]{32}\z') { throw 'Unexpected inspection cleanup path.' }
    if (Test-Path -LiteralPath $inspectionFull) { Remove-Item -LiteralPath $inspectionFull -Recurse -Force }
}
