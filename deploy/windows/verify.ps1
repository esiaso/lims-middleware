<# Post-install checks (design 10.3 step 9). #>
param([string] $BaseUrl = "https://localhost")
$ErrorActionPreference = "Stop"
$live = Invoke-RestMethod "$BaseUrl/health/live"
$ready = Invoke-RestMethod "$BaseUrl/health/ready"
Write-Host "live: $($live.status)  ready: $($ready.status)"
$drive = Get-PSDrive -Name D -ErrorAction SilentlyContinue
if ($drive) { Write-Host ("D: free {0:N1}%" -f (100 * $drive.Free / ($drive.Free + $drive.Used))) }
