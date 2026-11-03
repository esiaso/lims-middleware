<#
.SYNOPSIS
  Builds the offline install bundle (design 10.2): the MSI, the PostgreSQL installer, the lims-capture tool,
  the deploy scripts and docs, with SHA256SUMS.txt and bundle.json, zipped. Runs on the Windows CI runner,
  which has internet access; the result is carried to the hospital server on removable media.
  The application is published self-contained, so no .NET runtime is needed on the server. The PostgreSQL
  installer brings its own Visual C++ runtime (install_runtimes=1).
.EXAMPLE
  .\build-bundle.ps1 -Msi ..\..\installer\bin\x64\Release\LimsMiddleware-0.3.0-x64.msi -CaptureDir ..\..\artifacts\publish\lims-capture -Version 0.3.0
#>
param(
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $CaptureDir,
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutDir = (Join-Path $PSScriptRoot "..\..\artifacts\bundle"),
    [string] $Commit = $env:GITHUB_SHA,
    # Download cache, so a rebuild does not fetch the PostgreSQL installer again.
    [string] $CacheDir = (Join-Path $PSScriptRoot "..\..\artifacts\cache"),
    # Only for the first build after changing the PostgreSQL version: prints the hash to pin in prereqs.json.
    [switch] $AllowUnpinned
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$prereqs = Get-Content -Raw (Join-Path $PSScriptRoot "prereqs.json") | ConvertFrom-Json
$pg = $prereqs.postgres
$name = "LimsMiddleware-$Version-offline"
$stage = Join-Path $OutDir $name
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage, $CacheDir | Out-Null

# PostgreSQL installer: download once, check against the pinned hash.
$pgFile = Split-Path $pg.url -Leaf
$pgCached = Join-Path $CacheDir $pgFile
if (-not (Test-Path $pgCached)) {
    Write-Host "Downloading $($pg.url)"
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri $pg.url -OutFile "$pgCached.part" -UseBasicParsing
    Move-Item "$pgCached.part" $pgCached
}
$pgHash = (Get-FileHash -Algorithm SHA256 $pgCached).Hash
if (-not $pg.sha256) {
    if (-not $AllowUnpinned) { throw "prereqs.json has no sha256 for $pgFile. Downloaded hash: $pgHash. Pin it, or pass -AllowUnpinned once." }
    Write-Warning "UNPINNED: $pgFile sha256 $pgHash. Put this in deploy/bundle/prereqs.json."
} elseif ($pgHash -ne $pg.sha256.ToUpperInvariant()) {
    Remove-Item $pgCached
    throw "$pgFile hash $pgHash does not match the pinned $($pg.sha256)."
}
$sig = Get-AuthenticodeSignature $pgCached
Write-Host "PostgreSQL installer signature: $($sig.Status) $($sig.SignerCertificate.Subject)"
if ($sig.Status -ne "Valid") { throw "The PostgreSQL installer's Authenticode signature is $($sig.Status)." }

# Lay out the bundle.
$layout = @(
    @{ From = $Msi; To = "msi" },
    @{ From = $pgCached; To = "prereqs" },
    @{ From = (Join-Path $PSScriptRoot "Install-Offline.ps1"); To = "." },
    @{ From = (Join-Path $repo "deploy\windows\*.ps1"); To = "scripts\windows" },
    @{ From = (Join-Path $repo "deploy\sql\*.sql"); To = "scripts\sql" },
    @{ From = (Join-Path $repo "docs\BACKUP-RUNBOOK.md"); To = "docs" },
    @{ From = (Join-Path $repo "docs\IMPLEMENTATION-NOTES.md"); To = "docs" },
    @{ From = (Join-Path $repo "tests\captures\README.md"); To = "tools\lims-capture\CAPTURE-README.md" }
)
foreach ($item in $layout) {
    $dest = Join-Path $stage $item.To
    if ($item.To -like "*.md") { New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null }
    else { New-Item -ItemType Directory -Force -Path $dest | Out-Null }
    Copy-Item -Path $item.From -Destination $dest -Force
}
Copy-Item (Join-Path $repo "docs\OFFLINE-INSTALL.md") (Join-Path $stage "README-OFFLINE.md")
Copy-Item -Path (Join-Path $CaptureDir "*") -Destination (Join-Path $stage "tools\lims-capture") -Recurse -Force
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item

[ordered]@{
    version  = $Version
    commit   = $Commit
    builtAt  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    msi      = "msi/" + (Split-Path $Msi -Leaf)
    postgres = [ordered]@{ major = $pg.major; version = $pg.version; file = "prereqs/$pgFile"; sha256 = $pgHash; source = $pg.url }
    signed   = [ordered]@{ msi = ((Get-AuthenticodeSignature $Msi).Status -eq "Valid") }
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $stage "bundle.json")

# Hashes of everything except the list itself, with forward-slash relative paths.
$sums = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($stage.Length + 1).Replace("\", "/")
    "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash, $rel
}
$sums | Set-Content -Encoding ASCII (Join-Path $stage "SHA256SUMS.txt")

$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
"{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $zip).Hash, (Split-Path $zip -Leaf) | Set-Content -Encoding ASCII "$zip.sha256"
Write-Host "Bundle: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
Get-Content "$zip.sha256"
