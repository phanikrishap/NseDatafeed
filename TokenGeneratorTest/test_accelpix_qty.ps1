$apiKey = 'MC0r0yOiq2uR6u9ouaM7CIaZ9K0='
$encodedKey = [System.Uri]::EscapeDataString($apiKey)
$ticker = 'NIFTY2611325850CE'
$date = '20260109'
$url = "https://apidata.accelpix.in/api/fda/rest/ticks/$ticker/$date`?api_token=$encodedKey"

Write-Host "Fetching: $ticker for $date"
Write-Host ''

try {
    $response = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 60

    $total = $response.Count
    $withQty = ($response | Where-Object { $_.qt -gt 0 }).Count
    $withVol = ($response | Where-Object { $_.vol -gt 0 }).Count
    $zeroQty = $total - $withQty

    Write-Host "Total ticks: $total"
    Write-Host "Ticks with qty > 0: $withQty"
    Write-Host "Ticks with qty = 0: $zeroQty"
    Write-Host "Ticks with vol > 0: $withVol"
    Write-Host ''

    # Show sample ticks with non-zero qty
    Write-Host 'Sample ticks with qty > 0:'
    $withQtySample = $response | Where-Object { $_.qt -gt 0 } | Select-Object -First 5
    $epoch1980 = [datetime]'1980-01-01'

    $withQtySample | ForEach-Object {
        $tm = $_.tm
        $time = $epoch1980.AddSeconds($tm)
        Write-Host "  $($time.ToString('HH:mm:ss')) | Price: $($_.pr) | Qty: $($_.qt) | Vol: $($_.vol) | OI: $($_.oi)"
    }

    Write-Host ''
    Write-Host 'Sample ticks with qty = 0 (quote updates):'
    $zeroQtySample = $response | Where-Object { $_.qt -eq 0 } | Select-Object -First 5

    $zeroQtySample | ForEach-Object {
        $tm = $_.tm
        $time = $epoch1980.AddSeconds($tm)
        Write-Host "  $($time.ToString('HH:mm:ss')) | Price: $($_.pr) | Qty: $($_.qt) | Vol: $($_.vol) | OI: $($_.oi)"
    }
} catch {
    Write-Host "Error: $($_.Exception.Message)"
}
