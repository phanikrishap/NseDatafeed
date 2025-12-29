# Build and Deploy ZerodhaDatafeedAdapter to NinjaTrader
# Run this script from PowerShell

$ErrorActionPreference = "Stop"

$ProjectPath = "d:\CascadeProjects\NseDatafeed_new\ZerodhaDatafeedAdapter\ZerodhaDatafeedAdapter.csproj"
$SourceDll = "d:\CascadeProjects\NseDatafeed_new\ZerodhaDatafeedAdapter\bin\Debug\ZerodhaDatafeedAdapter.dll"
$SourceApiDll = "d:\CascadeProjects\NseDatafeed_new\ZerodhaAPI\bin\Debug\ZerodhaAPI.dll"
$DestDll = "C:\Users\Phani Krishna\Documents\NinjaTrader 8\bin\Custom\ZerodhaDatafeedAdapter.dll"
$DestApiDll = "C:\Users\Phani Krishna\Documents\NinjaTrader 8\bin\Custom\ZerodhaAPI.dll"
$MSBuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

Write-Host "=== ZerodhaDatafeedAdapter Build & Deploy ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build
Write-Host "[1/3] Building project..." -ForegroundColor Yellow
& "$MSBuild" "$ProjectPath" /t:Build /p:Configuration=Debug /verbosity:minimal

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED!" -ForegroundColor Red
    exit 1
}
Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Step 2: Verify source DLL exists
Write-Host "[2/3] Verifying build output..." -ForegroundColor Yellow
if (-not (Test-Path $SourceDll)) {
    Write-Host "ERROR: Source DLL not found at: $SourceDll" -ForegroundColor Red
    exit 1
}
$sourceInfo = Get-Item $SourceDll
Write-Host "Source DLL: $SourceDll" -ForegroundColor Gray
Write-Host "  Size: $($sourceInfo.Length) bytes" -ForegroundColor Gray
Write-Host "  Modified: $($sourceInfo.LastWriteTime)" -ForegroundColor Gray
Write-Host ""

# Step 3: Copy to NinjaTrader
Write-Host "[3/3] Copying to NinjaTrader..." -ForegroundColor Yellow
try {
    # Copy ZerodhaDatafeedAdapter.dll
    Copy-Item -Path $SourceDll -Destination $DestDll -Force
    Write-Host "Copied ZerodhaDatafeedAdapter.dll successfully!" -ForegroundColor Green
    $destInfo = Get-Item $DestDll
    Write-Host "  Destination: $DestDll" -ForegroundColor Gray
    Write-Host "  Size: $($destInfo.Length) bytes" -ForegroundColor Gray

    # Copy ZerodhaAPI.dll
    Copy-Item -Path $SourceApiDll -Destination $DestApiDll -Force
    Write-Host "Copied ZerodhaAPI.dll successfully!" -ForegroundColor Green
    $destApiInfo = Get-Item $DestApiDll
    Write-Host "  Destination: $DestApiDll" -ForegroundColor Gray
    Write-Host "  Size: $($destApiInfo.Length) bytes" -ForegroundColor Gray
} catch {
    Write-Host "ERROR: Failed to copy DLL. Is NinjaTrader running?" -ForegroundColor Red
    Write-Host "Close NinjaTrader and try again." -ForegroundColor Yellow
    Write-Host "Error: $_" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== DEPLOY COMPLETE ===" -ForegroundColor Green
Write-Host "Restart NinjaTrader to load the new DLL." -ForegroundColor Cyan
