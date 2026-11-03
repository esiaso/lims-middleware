<#
.SYNOPSIS
  Authenticode-signs files with a code-signing certificate from a PFX (SHA-256, RFC 3161 timestamp), then checks
  each signature. Used by CI when the SIGNING_PFX_BASE64 and SIGNING_PFX_PASSWORD secrets are set (design 10.2).
.EXAMPLE
  .\sign.ps1 -Files artifacts\publish\win-x64\Lims.Host.exe -PfxPath C:\temp\sign.pfx -PfxPassword $pw
#>
param(
    [Parameter(Mandatory)] [string[]] $Files,
    [Parameter(Mandatory)] [string] $PfxPath,
    [Parameter(Mandatory)] [securestring] $PfxPassword,
    [string] $TimestampUrl = "http://timestamp.digicert.com",
    [string] $Description = "LIMS Middleware",
    # Only for tests with a throwaway certificate: accept a chain that is not trusted on this machine.
    [switch] $AllowUntrustedRoot
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } | Select-Object -Last 1
if (-not $signtool) { throw "signtool.exe not found (Windows 10 SDK)." }

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword)
try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $plain)
if (-not ($cert.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
        ($_.EnhancedKeyUsages | Where-Object Value -eq "1.3.6.1.5.5.7.3.3") })) {
    throw "Certificate $($cert.Subject) is not a code-signing certificate (EKU 1.3.6.1.5.5.7.3.3)."
}
if ($cert.NotAfter -lt (Get-Date)) { throw "Certificate $($cert.Subject) expired on $($cert.NotAfter)." }
Write-Host "Signing with $($cert.Subject) (thumbprint $($cert.Thumbprint), valid to $($cert.NotAfter.ToString('yyyy-MM-dd')))"

foreach ($file in $Files) {
    $full = (Resolve-Path $file).Path
    $attempt = 0
    while ($true) {
        # signtool takes the password on its command line; CI masks the secret in logs.
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $TimestampUrl /d $Description /f $PfxPath /p $plain $full | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        # The timestamp server is the usual cause of a failure; retry a few times.
        if (++$attempt -ge 3) { throw "signtool failed on $full (exit $LASTEXITCODE)" }
        Start-Sleep -Seconds (5 * $attempt)
    }
    $sig = Get-AuthenticodeSignature $full
    $ok = $sig.Status -eq "Valid" -or ($AllowUntrustedRoot -and $sig.Status -eq "UnknownError")
    if (-not $ok -or $sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) { throw "Signature check failed on ${full}: $($sig.Status) $($sig.StatusMessage)" }
    if (-not $sig.TimeStamperCertificate) { throw "No timestamp on $full" }
    Write-Host "Signed $full ($($sig.Status))"
}
