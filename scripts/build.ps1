# Builds House Control for Windows: the web UI, the tests, the app for each architecture,
# a portable .zip and an installer, each with its SHA-256.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1                 # x64 and arm64, Release
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Arch x64 -SkipTests
#
# Needs the .NET 8 SDK, Node.js 20.19+ and, for the installer, Inno Setup 6 (winget install JRSoftware.InnoSetup).
param(
  [string[]]$Arch = @('x64', 'arm64'),
  [string]$Version,
  [switch]$SkipTests,
  [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

if (-not $Version) {
  $Version = ([xml](Get-Content src\HouseControl\HouseControl.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "House Control $Version" -ForegroundColor Cyan

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run($exe) {
  & $exe @args
  if ($LASTEXITCODE -ne 0) { throw "$exe $args failed ($LASTEXITCODE)" }
}

Step 'Web UI'
Push-Location web
if (-not (Test-Path node_modules)) { Run npm.cmd ci --no-audit --no-fund }
Run npm.cmd run build
Pop-Location

if (-not $SkipTests) {
  Step 'Tests'
  Run dotnet test tests\HouseControl.Core.Tests -c Release --nologo
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1

foreach ($a in $Arch) {
  Step "App for $a"
  $out = Join-Path $root "artifacts\publish\win-$a"
  if (Test-Path $out) { Remove-Item $out -Recurse -Force }
  Run dotnet publish src\HouseControl -c Release -r "win-$a" --self-contained true -p:PublishReadyToRun=true `
    -p:Version=$Version -p:DebugType=none -o $out --nologo
  if (-not (Test-Path "$out\web\index.html")) { throw "The web UI is missing from $out" }

  Step "Portable zip for $a"
  $zip = Join-Path $dist "HouseControl-$Version-win-$a.zip"
  if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path "$out\*" -DestinationPath $zip -CompressionLevel Optimal

  if (-not $SkipInstaller) {
    if (-not $iscc) { throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup), or pass -SkipInstaller' }
    Step "Installer for $a"
    Run $iscc /Qp "/DMyVersion=$Version" "/DMyArch=$a" "/DSourceDir=$out" "/DOutputDir=$dist" installer\HouseControl.iss
  }
}

Step 'Checksums'
Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.zip', '.exe' -and $_.Name -like "*$Version*" } | ForEach-Object {
  $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -Path "$($_.FullName).sha256" -Value "$hash  $($_.Name)" -Encoding ascii -NoNewline
  Write-Host ("{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host "`nDone: $dist" -ForegroundColor Green
