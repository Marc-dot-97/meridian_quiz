<#
 Meridian - PROD settings. Run ON THE PROD SERVER (10.10.1.2), elevated PowerShell, over RDP.
 It asks for the two secrets (typed hidden, never saved to a file, never shown) and stores every
 setting as an ENVIRONMENT VARIABLE OF THE MERIDIAN APP POOL (inside IIS config, not in the site folder),
 so a redeploy can never wipe or overwrite them.
 Safe to re-run: it replaces the whole set each time. A backup of the IIS config is taken first.
   powershell -ExecutionPolicy Bypass -File <path>\set-meridian-prod-settings.ps1
#>
param([string]$SiteName = 'Meridian')
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this in an ELEVATED PowerShell (Run as administrator).' }
Import-Module WebAdministration

function Plain([Security.SecureString]$s) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }

$site = Get-Website -Name $SiteName
if (-not $site) { throw "No IIS site called '$SiteName'. Re-run with -SiteName '<name>'." }
$pool = $site.applicationPool
Write-Host "Site '$SiteName' uses app pool '$pool'." -ForegroundColor Cyan

$dbHost = Read-Host 'MySQL server for Meridian (Enter = 127.0.0.1, i.e. MySQL runs on this same server)'
if (-not $dbHost) { $dbHost = '127.0.0.1' }
$dbPw   = Plain (Read-Host 'Password of the MySQL login meridian_app (the PROD one you set in the SQL script)' -AsSecureString)
$clientId = Read-Host 'Azure Application (client) ID (Enter = 44b20727-2f23-4bee-9a47-bb1361a77d5b)'
if (-not $clientId) { $clientId = '44b20727-2f23-4bee-9a47-bb1361a77d5b' }
$secret = Plain (Read-Host 'Azure client secret VALUE (not the Secret ID)' -AsSecureString)
$super  = Read-Host 'SuperAdmin emails, comma separated (Enter = marc@optimumgroup.co.za)'
if (-not $super) { $super = 'marc@optimumgroup.co.za' }
$hr     = Read-Host 'HR emails, comma separated (Enter to leave empty for now)'
$keys   = 'C:\inetpub\apps\Meridian-data\keys'

if (-not $dbPw -or -not $secret) { throw 'Both passwords/secrets are required.' }
if ($dbPw -match '[;=''"\s]') { throw 'The MySQL password contains ; = quote or a space - it would break the connection string. Use letters, digits, - or _.' }
if ($secret -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-') { throw 'That looks like a Secret ID (a GUID), not the secret VALUE. Copy the Value column in Azure.' }

New-Item -ItemType Directory -Force -Path $keys | Out-Null
icacls $keys /grant "IIS AppPool\${pool}:(OI)(CI)M" | Out-Null

$vars = [ordered]@{
  'ASPNETCORE_ENVIRONMENT'      = 'Production'
  'DevBypass__Enabled'          = 'false'
  'ConnectionStrings__MeridianDb' = "Server=$dbHost;Port=3306;Database=optimum_meridian;User ID=meridian_app;Password=$dbPw;"
  'Directory__Source'           = 'crm'
  'Directory__AdminDatabase'    = 'optimum_admin'
  'Directory__ConnectionString' = "Server=$dbHost;Port=3306;Database=optimum_admin;User ID=meridian_app;Password=$dbPw;"
  'AzureAd__TenantId'           = 'cf36daf0-75ab-470e-a3d8-6877ecbcba75'
  'AzureAd__ClientId'           = $clientId
  'AzureAd__ClientSecret'       = $secret
  'DataProtection__KeysPath'    = $keys
}
$i = 0; foreach ($e in ($super -split '[,;\s]+' | Where-Object { $_ })) { $vars["Reports__SuperAdminEmails__$i"] = $e.Trim(); $i++ }
$i = 0; foreach ($e in ($hr    -split '[,;\s]+' | Where-Object { $_ })) { $vars["Reports__HrEmails__$i"]         = $e.Trim(); $i++ }

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
Backup-WebConfiguration -Name "pre-meridian-settings-$stamp" | Out-Null
Write-Host "IIS config backed up as 'pre-meridian-settings-$stamp' (undo: Restore-WebConfiguration -Name pre-meridian-settings-$stamp)" -ForegroundColor DarkGray

$filter = "system.applicationHost/applicationPools/add[@name='$pool']/environmentVariables"
Clear-WebConfiguration -Filter $filter -PSPath 'MACHINE/WEBROOT/APPHOST'
foreach ($k in $vars.Keys) { Add-WebConfiguration -Filter $filter -PSPath 'MACHINE/WEBROOT/APPHOST' -Value @{ name = $k; value = [string]$vars[$k] } }

Write-Host "`nSettings stored on app pool '$pool' (values hidden):" -ForegroundColor Green
$vars.Keys | ForEach-Object { Write-Host "  $_" }
$secretNames = 'ConnectionStrings__MeridianDb','Directory__ConnectionString','AzureAd__ClientSecret'
Write-Host "`nSecret values are inside IIS's configuration, readable by server administrators only." -ForegroundColor DarkGray
if ((Get-WebAppPoolState $pool).Value -eq 'Started') { Restart-WebAppPool $pool; Write-Host "App pool '$pool' restarted." }
