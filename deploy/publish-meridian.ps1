<#
 Meridian - build the PROD package on YOUR PC (changes nothing on any server).
 Run in PowerShell on your PC:   powershell -ExecutionPolicy Bypass -File C:\Dev\meridian_quiz\deploy\publish-meridian.ps1
 Result: C:\Dev\publish-meridian  (this is what the PROD deploy script copies from)
#>
$ErrorActionPreference = 'Stop'
$repo = 'C:\Dev\meridian_quiz'
$out  = 'C:\Dev\publish-meridian'
Set-Location $repo

$dirty = git status --porcelain --untracked-files=no
if ($dirty) { Write-Host $dirty; throw 'You have uncommitted changes in tracked files. Commit or stash them first, then run this again.' }

git fetch origin
git checkout main
git pull origin main
$commit = (git rev-parse --short HEAD).Trim()
Write-Host "Building from main at commit $commit" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\Meridian.Api\Meridian.Api.csproj -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed - send me the error lines above.' }

# --- sanity checks -----------------------------------------------------------------
$problems = @()
foreach ($f in 'Meridian.Api.dll','web.config') { if (-not (Test-Path (Join-Path $out $f))) { $problems += "missing $f" } }
$fw = Join-Path $out 'wwwroot\_framework'
if (-not (Test-Path $fw) -or (Get-ChildItem $fw -File -Recurse | Measure-Object).Count -lt 20) { $problems += 'the Blazor client (wwwroot\_framework) is missing or incomplete' }
foreach ($bad in 'appsettings.Development.json','appsettings.Production.json','.env') { if (Test-Path (Join-Path $out $bad)) { $problems += "$bad must NOT be in the package" } }
if ($problems) { $problems | ForEach-Object { Write-Host "PROBLEM: $_" -ForegroundColor Red }; throw 'Package check failed - do not deploy. Send me the PROBLEM lines.' }

$count = (Get-ChildItem $out -File -Recurse | Measure-Object).Count
$hash  = (Get-FileHash (Join-Path $out 'Meridian.Api.dll') -Algorithm SHA256).Hash.Substring(0,16)
Write-Host "OK: $count files in $out  |  commit $commit  |  Meridian.Api.dll sha256 $hash..." -ForegroundColor Green
