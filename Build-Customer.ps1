$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler not found.' }
$framework = Split-Path -Parent $compiler
$source = Join-Path $PSScriptRoot 'customer\Customer.cs'
$files = Join-Path $PSScriptRoot 'customer\CustomerFiles.cs'
$exe = Join-Path $PSScriptRoot 'DLI-Customer.exe'
& $compiler '/nologo' '/target:winexe' '/optimize+' '/codepage:65001' "/out:$exe" `
    '/r:System.Drawing.dll' '/r:System.Windows.Forms.dll' `
    "/r:$framework\System.Net.Http.dll" "/r:$framework\System.Web.Extensions.dll" $source $files
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
Write-Output $exe
