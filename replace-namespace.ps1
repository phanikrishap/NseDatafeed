Get-ChildItem -Path 'd:\CascadeProjects\NseDatafeed_new\ZerodhaAPI' -Recurse -Filter '*.cs' | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $newContent = $content -replace 'namespace ZerodhaAPI', 'namespace ZerodhaAPI' -replace 'using ZerodhaAPI', 'using ZerodhaAPI'
    if ($content -ne $newContent) {
        Set-Content $_.FullName -Value $newContent -NoNewline
        Write-Host "Updated: $($_.FullName)"
    }
}
Write-Host "Done!"
