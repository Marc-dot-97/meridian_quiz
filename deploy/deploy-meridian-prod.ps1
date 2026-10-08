<#
 Meridian - deploy the published package to the PROD IIS site. Run ON THE PROD SERVER (10.10.1.2),
 elevated PowerShell, over RDP, AFTER publish-meridian.ps1 has built C:\Dev\publish-meridian on your PC
 and set-meridian-prod-settings.ps1 has stored the settings.

   -DryRun                       only list what would change
   (no switch)                   backup -> take site offline -> mirror files -> bring back -> smoke test
   -Rollback -Stamp <stamp>      put the backup made by an earlier run back (stamp is printed by that run)

 Never touched: appsettings.Production.json, logs, App_Data. The site's settings live on the app pool
 (set-meridian-prod-settings.ps1), so this script never sees or copies a secret.
#>
param(
  [string]$SiteName = 'Meridian',
  [string]$Source   = '\\tsclient\C\Dev\publish-meridian',
  [string]$Url      = 'https://meridian.primebridge.co.za',
  [switch]$DryRun,
  [switch]$Rollback,
  [string]$Stamp
)
$ErrorActionPreference = 'Stop'
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this in an ELEVATED PowerShell (Run as administrator).' }
Import-Module WebAdministration

$site = Get-Website -Name $SiteName
if (-not $site) {
  Say "No IIS site called '$SiteName'. Sites on this server:" Yellow
  Get-Website | ForEach-Object { Say "  $($_.Name)  ->  $($_.physicalPath)" }
  throw "Re-run with -SiteName '<one of the names above>'."
}
$target = [Environment]::ExpandEnvironmentVariables($site.physicalPath).TrimEnd('\')
$pool   = $site.applicationPool
Say "Site '$SiteName' | folder $target | app pool '$pool'" Cyan

function Go-Offline  { Set-Content -Path (Join-Path $target 'app_offline.htm') -Value '<html><body style="font-family:sans-serif"><h2>Meridian is being updated</h2><p>Back in a minute.</p></body></html>'; Start-Sleep -Seconds 4 }
function Go-Online   {
  Remove-Item (Join-Path $target 'app_offline.htm') -Force -ErrorAction SilentlyContinue
  if ((Get-WebAppPoolState $pool).Value -ne 'Started') { Start-WebAppPool $pool }
  if ($site.State -ne 'Started') { Start-Website $SiteName }
}

# ---------------------------------------------------------------- rollback
if ($Rollback) {
  if (-not $Stamp) { throw 'Rollback needs -Stamp <stamp>, e.g. -Stamp 20261006-1830' }
  $bak = "$target-bak-$Stamp"
  if (-not (Test-Path $bak)) { throw "Backup folder not found: $bak" }
  Say "ROLLBACK: restore $bak -> $target" Yellow
  if ($DryRun) { robocopy $bak $target /MIR /L /XF app_offline.htm appsettings.Production.json /XD logs App_Data /NP /NJH /NJS | Select-Object -First 60; return }
  Go-Offline
  robocopy $bak $target /MIR /XF app_offline.htm appsettings.Production.json /XD logs App_Data /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE). The site is left offline; send me this message." }
  Go-Online
  Say 'Rollback done. Site is back online with the previous files.' Green
  return
}

# ---------------------------------------------------------------- checks on the package
if (-not (Test-Path $Source)) { throw "Cannot see $Source. In the RDP session your PC's C: drive is \\tsclient\C - check 'Drives' is shared in the RDP settings and that publish-meridian.ps1 ran." }
foreach ($f in 'Meridian.Api.dll', 'web.config') { if (-not (Test-Path (Join-Path $Source $f))) { throw "Package is missing $f" } }
$fw = Join-Path $Source 'wwwroot\_framework'
if (-not (Test-Path $fw) -or (Get-ChildItem $fw -File -Recurse | Measure-Object).Count -lt 20) { throw 'Package has no complete Blazor client (wwwroot\_framework).' }
foreach ($bad in 'appsettings.Development.json', 'appsettings.Production.json', '.env') { if (Test-Path (Join-Path $Source $bad)) { throw "$bad must not be in the package. Re-run publish-meridian.ps1." } }
# Safety: the Meridian folder must not be (or contain, or sit inside) any OTHER IIS site's folder, e.g. the CRM.
$others = Get-Website | Where-Object { $_.Name -ne $SiteName } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_.physicalPath).TrimEnd('\') }
foreach ($o in $others) {
  if ($target -ieq $o -or $target -like "$o\*" -or $o -like "$target\*") { throw "Site folder $target overlaps another IIS site's folder ($o) - stopping to be safe." }
}
if ($target.Length -le 3 -or $target -like 'C:\Windows*' -or $target -like 'C:\Program Files*') { throw "Unexpected site folder $target - stopping to be safe." }

$srcHash = (Get-FileHash (Join-Path $Source 'Meridian.Api.dll') -Algorithm SHA256).Hash
Say "Package OK. Meridian.Api.dll sha256 $($srcHash.Substring(0,16))..." Green

# ---------------------------------------------------------------- dry run
$excl = @('/XF', 'app_offline.htm', 'appsettings.Production.json', '/XD', 'logs', 'App_Data')
if ($DryRun) {
  Say 'DRY RUN - nothing is changed. Files that would be added/updated/removed:' Yellow
  New-Item -ItemType Directory -Force -Path $target | Out-Null
  robocopy $Source $target /MIR /L @excl /NP /NJH /NDL | Select-Object -Last 15
  return
}

# ---------------------------------------------------------------- real deploy
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$bak = "$target-bak-$stamp"
New-Item -ItemType Directory -Force -Path $target | Out-Null
$hadFiles = [bool](Get-ChildItem $target -Force -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($hadFiles) {
  Say "Backing up current site to $bak ..." Cyan
  robocopy $target $bak /E /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "Backup failed ($LASTEXITCODE) - nothing was changed." }
} else { Say 'Site folder is empty (first deploy) - no backup needed. To undo a first deploy simply stop the site in IIS.' DarkGray }

Say 'Taking the site offline and copying files ...' Cyan
Go-Offline
robocopy $Source $target /MIR @excl /NFL /NDL /NJH /NJS /NP | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) { throw "Copy failed ($rc). The site is left offline. Rollback: -Rollback -Stamp $stamp" }
Go-Online

$liveHash = (Get-FileHash (Join-Path $target 'Meridian.Api.dll') -Algorithm SHA256).Hash
if ($liveHash -ne $srcHash) { Say 'WARNING: live Meridian.Api.dll does not match the package!' Red } else { Say 'Live Meridian.Api.dll matches the package.' Green }

Say "Smoke test (first start creates the database tables and can take up to a minute) ..." Cyan
# The server cannot always reach its own public address, so test through this machine directly (still the real HTTPS name and certificate).
$hostName = ([Uri]$Url).Host
$out = & curl.exe -sS -i --max-time 120 --resolve "${hostName}:443:127.0.0.1" "$Url/auth/status" 2>&1
$first = ($out | Select-Object -First 1)
if ("$first" -match ' 200') { Say "  $Url/auth/status -> $first" Green; ($out | Select-Object -Last 1) | ForEach-Object { Say "  $_" Green } }
else {
  Say "  $Url/auth/status -> $first" Red
  Say '  500.30 = the app failed to start. See the reason with:' Yellow
  Say "  Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='IIS AspNetCore Module V2'} -MaxEvents 3 | Format-List TimeCreated, Message" Yellow
  Say "  Rollback if needed:  -Rollback -Stamp $stamp" Yellow
}
Say "`nDone. Rollback stamp: $stamp  (backup folder: $bak)" Cyan
