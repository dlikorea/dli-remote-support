function Get-DliReleaseKey([string]$KeyPath, [switch]$Create) {
    Add-Type -AssemblyName System.Security
    $fullPath = [IO.Path]::GetFullPath($KeyPath)
    $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072, [Security.Cryptography.CspParameters]::new(24))
    $rsa.PersistKeyInCsp = $false
    try {
        $scope = [Security.Cryptography.DataProtectionScope]::CurrentUser
        if ([IO.File]::Exists($fullPath)) {
            $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($fullPath), $null, $scope)
            try { $rsa.ImportCspBlob($plain) }
            finally { [Array]::Clear($plain, 0, $plain.Length) }
        } elseif ($Create) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
            $plain = $rsa.ExportCspBlob($true)
            try {
                $protected = [Security.Cryptography.ProtectedData]::Protect($plain, $null, $scope)
                $stream = [IO.File]::Open($fullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $stream.Write($protected, 0, $protected.Length) }
                finally { $stream.Dispose() }
            } finally { [Array]::Clear($plain, 0, $plain.Length) }
        } else { throw 'Release signing key not found.' }
        return $rsa
    } catch { $rsa.Dispose(); throw }
}
