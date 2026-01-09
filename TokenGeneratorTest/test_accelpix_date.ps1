$apiKey = 'MC0r0yOiq2uR6u9ouaM7CIaZ9K0='
$encodedKey = [System.Uri]::EscapeDataString($apiKey)
$ticker = 'NIFTY2611325850CE'
$date = '20260109'
$url = "https://apidata.accelpix.in/api/fda/rest/ticks/$ticker/$date`?api_token=$encodedKey"

Write-Host "Fetching: $url"
Write-Host ''

try {
    $response = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 60

    Write-Host "Total ticks: $($response.Count)"
    Write-Host ''
    Write-Host 'First 5 ticks (raw JSON):'

    $first5 = $response | Select-Object -First 5
    $first5 | ForEach-Object {
        $json = $_ | ConvertTo-Json -Compress
        Write-Host $json
    }

    Write-Host ''
    Write-Host 'Analyzing tm (time) field:'
    $epoch1980 = [datetime]'1980-01-01'
    $epoch1970 = [datetime]'1970-01-01'

    $first5 | ForEach-Object {
        $tm = $_.tm
        $as1980 = $epoch1980.AddSeconds($tm)
        $as1970 = $epoch1970.AddSeconds($tm)
        Write-Host "  tm=$tm -> 1980 epoch: $($as1980.ToString('yyyy-MM-dd HH:mm:ss')) | 1970 epoch: $($as1970.ToString('yyyy-MM-dd HH:mm:ss'))"
    }

    Write-Host ''
    Write-Host 'Last 3 ticks:'
    $last3 = $response | Select-Object -Last 3
    $last3 | ForEach-Object {
        $tm = $_.tm
        $as1980 = $epoch1980.AddSeconds($tm)
        $as1970 = $epoch1970.AddSeconds($tm)
        Write-Host "  tm=$tm -> 1980 epoch: $($as1980.ToString('yyyy-MM-dd HH:mm:ss')) | 1970 epoch: $($as1970.ToString('yyyy-MM-dd HH:mm:ss'))"
    }
} catch {
    Write-Host "Error: $($_.Exception.Message)"
}
