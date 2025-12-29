$output = dotnet build ZerodhaDatafeedAdapter.csproj
$output | Out-File -FilePath "build_results.txt"
Write-Host "Build results saved to build_results.txt"
