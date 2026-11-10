<#
 Meridian - PROD: folder for the "Watch first" module videos. Run ON THE PROD SERVER, elevated PowerShell, over RDP,
 BEFORE deploying the release that adds module videos (the app creates/uses this folder at start-up).
 Creates C:\inetpub\apps\Meridian-data\quiz-videos (outside the site, so deploys and rollbacks never touch it),
 gives the app pool Modify rights, and adds ONLY the Videos__Folder variable to the app pool (other settings are kept).
 Safe to re-run. set-meridian-prod-settings.ps1 sets the same variable, so a later full settings run keeps it.
   powershell -ExecutionPolicy Bypass -File \\tsclient\C\Dev\meridian_quiz\deploy\set-meridian-video-folder.ps1
#>
param([string]$SiteName = 'Meridian', [string]$Folder = 'C:\inetpub\apps\Meridian-data\quiz-videos')
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this in an ELEVATED PowerShell (Run as administrator).' }
Import-Module WebAdministration
$site = Get-Website -Name $SiteName
if (-not $site) { throw "No IIS site called '$SiteName'. Re-run with -SiteName '<name>'." }
$pool = $site.applicationPool

New-Item -ItemType Directory -Force -Path $Folder | Out-Null
icacls $Folder /grant "IIS AppPool\${pool}:(OI)(CI)M" | Out-Null
Write-Host "Folder ready: $Folder (Modify for IIS AppPool\$pool)" -ForegroundColor Green

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
Backup-WebConfiguration -Name "pre-meridian-videos-$stamp" | Out-Null
$filter = "system.applicationHost/applicationPools/add[@name='$pool']/environmentVariables"
$existing = Get-WebConfiguration -Filter "$filter/add[@name='Videos__Folder']" -PSPath 'MACHINE/WEBROOT/APPHOST'
if ($existing) { Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -AtElement @{ name = 'Videos__Folder' } }
Add-WebConfiguration -Filter $filter -PSPath 'MACHINE/WEBROOT/APPHOST' -Value @{ name = 'Videos__Folder'; value = $Folder }
Write-Host "App pool '$pool': Videos__Folder = $Folder (IIS config backup 'pre-meridian-videos-$stamp')." -ForegroundColor Green
Write-Host 'The deploy recycles the app pool, so no restart is needed now.' -ForegroundColor DarkGray
