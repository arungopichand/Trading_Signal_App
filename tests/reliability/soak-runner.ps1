param(
    [string]$BaseUrl = "http://localhost:10000",
    [int]$DurationMinutes = 30,
    [int]$Concurrency = 12,
    [string[]]$Symbols = @("AAPL","MSFT","NVDA","TSLA","AMD","META","AMZN","GOOGL","SPY","QQQ","PLTR","NFLX")
)

$ErrorActionPreference = "Stop"
$deadline = (Get-Date).ToUniversalTime().AddMinutes($DurationMinutes)
$latencies = New-Object System.Collections.Concurrent.ConcurrentBag[double]
$errors = [System.Collections.Concurrent.ConcurrentDictionary[string,int]]::new()
$requests = 0

Write-Host "Starting soak test for $DurationMinutes minute(s), concurrency=$Concurrency base=$BaseUrl"

$job = 1..$Concurrency | ForEach-Object {
    Start-Job -ArgumentList $BaseUrl, $Symbols, $deadline -ScriptBlock {
        param($BaseUrl, $Symbols, $Deadline)
        $rand = New-Object System.Random
        $localLatencies = @()
        $localErrors = @{}
        while((Get-Date).ToUniversalTime() -lt $Deadline) {
            $symbol = $Symbols[$rand.Next(0, $Symbols.Count)]
            $url = "$BaseUrl/api/market/unified/$symbol"
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                $resp = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 15
                if ($null -eq $resp) {
                    if (-not $localErrors.ContainsKey("null_response")) { $localErrors["null_response"] = 0 }
                    $localErrors["null_response"]++
                }
            } catch {
                if (-not $localErrors.ContainsKey("request_failure")) { $localErrors["request_failure"] = 0 }
                $localErrors["request_failure"]++
            } finally {
                $sw.Stop()
                $localLatencies += $sw.Elapsed.TotalMilliseconds
            }
            Start-Sleep -Milliseconds 120
        }
        [PSCustomObject]@{
            latencies = $localLatencies
            errors = $localErrors
        }
    }
}

Wait-Job -Job $job | Out-Null
$results = Receive-Job -Job $job
Remove-Job -Job $job | Out-Null

foreach ($result in $results) {
    foreach ($lat in $result.latencies) {
        $latencies.Add([double]$lat)
    }
    foreach ($entry in $result.errors.GetEnumerator()) {
        $errors.AddOrUpdate([string]$entry.Key, [int]$entry.Value, { param($k,$v) $v + [int]$entry.Value }) | Out-Null
    }
}

$samples = $latencies.ToArray() | Sort-Object
$count = $samples.Count
if ($count -eq 0) {
    throw "No latency samples collected."
}

function Get-Percentile([double[]]$vals, [double]$pct) {
    if ($vals.Count -eq 0) { return 0 }
    $index = [Math]::Min($vals.Count - 1, [Math]::Floor(($pct / 100.0) * ($vals.Count - 1)))
    return [Math]::Round($vals[$index], 2)
}

$p50 = Get-Percentile $samples 50
$p95 = Get-Percentile $samples 95
$avg = [Math]::Round(($samples | Measure-Object -Average).Average, 2)

$health = Invoke-RestMethod -Uri "$BaseUrl/api/health" -Method Get -TimeoutSec 15

$summary = [ordered]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    duration_minutes = $DurationMinutes
    concurrency = $Concurrency
    requests_observed = $count
    latency_ms = @{
        p50 = $p50
        p95 = $p95
        avg = $avg
    }
    error_counts = $errors
    market_data_metrics = $health.marketData
}

$outFile = Join-Path $PSScriptRoot "soak-summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $outFile -Encoding UTF8
Write-Host "Soak summary written to $outFile"
