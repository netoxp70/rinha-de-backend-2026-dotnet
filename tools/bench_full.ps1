# Full benchmark script — measures p50/p95/p99/max, RPS, failures
param(
    [int]$TotalRequests = 3000,
    [int]$Concurrency   = 50,
    [string]$Url        = "http://localhost:9999/fraud-score"
)

$payloads = @(
    # legit — score=0
    '{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}',
    # fraud — score=1
    '{"id":"tx-F1","transaction":{"amount":4368.82,"installments":8,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-015","MERC-017","MERC-007"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}',
    # legit no last_tx
    '{"id":"tx-L2","transaction":{"amount":120.00,"installments":1,"requested_at":"2026-04-01T10:00:00Z"},"customer":{"avg_amount":110.00,"tx_count_24h":1,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5812","avg_amount":100.00},"terminal":{"is_online":true,"card_present":true,"km_from_home":2.5},"last_transaction":null}',
    # borderline
    '{"id":"tx-B1","transaction":{"amount":500.00,"installments":3,"requested_at":"2026-05-10T15:30:00Z"},"customer":{"avg_amount":450.00,"tx_count_24h":5,"known_merchants":["MERC-010","MERC-020"]},"merchant":{"id":"MERC-030","mcc":"5999","avg_amount":480.00},"terminal":{"is_online":true,"card_present":false,"km_from_home":15.0},"last_transaction":{"timestamp":"2026-05-10T14:00:00Z","km_from_current":5.0}}'
)

# ---------- Warmup ----------
Write-Host "Warmup (50 reqs)..."
1..50 | ForEach-Object {
    try { $null = Invoke-RestMethod $Url -Method POST -ContentType "application/json" -Body $payloads[$_ % $payloads.Count] -TimeoutSec 10 } catch {}
}
Write-Host "Warmup done."

# ---------- Main benchmark ----------
$latencies = [System.Collections.Concurrent.ConcurrentBag[double]]::new()
$failures  = [System.Collections.Concurrent.ConcurrentBag[string]]::new()
$scores    = [System.Collections.Concurrent.ConcurrentBag[double]]::new()

$sw = [Diagnostics.Stopwatch]::StartNew()

1..$TotalRequests | ForEach-Object -ThrottleLimit $Concurrency -Parallel {
    $lat  = $using:latencies
    $fail = $using:failures
    $sc   = $using:scores
    $pay  = $using:payloads
    $u    = $using:Url
    $idx  = $_ % $pay.Count
    try {
        $t = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-RestMethod $u -Method POST -ContentType "application/json" -Body $pay[$idx] -TimeoutSec 10
        $t.Stop()
        $lat.Add($t.Elapsed.TotalMilliseconds)
        $sc.Add([double]$r.fraud_score)
    } catch {
        $fail.Add($_.Exception.Message)
        $lat.Add(10000)  # penalty
    }
}

$sw.Stop()
$elapsed = $sw.Elapsed.TotalSeconds

# ---------- Statistics ----------
$sorted = $latencies | Sort-Object
$n      = $sorted.Count
$rps    = [math]::Round($n / $elapsed, 1)

function Pct([double[]]$s, [double]$p) { $s[[int][math]::Floor($s.Count * $p)] }

$avg = [math]::Round(($sorted | Measure-Object -Average).Average, 2)
$p50 = [math]::Round((Pct $sorted 0.50), 2)
$p75 = [math]::Round((Pct $sorted 0.75), 2)
$p90 = [math]::Round((Pct $sorted 0.90), 2)
$p95 = [math]::Round((Pct $sorted 0.95), 2)
$p99 = [math]::Round((Pct $sorted 0.99), 2)
$max = [math]::Round($sorted[-1], 2)
$min = [math]::Round($sorted[0], 2)

# failure rate (exclude penalty entries)
$realFail = $failures.Count
$failPct  = [math]::Round($realFail / $TotalRequests * 100, 2)

# score distribution
$scoreArr = @($scores.ToArray())
$scoreFreq = $scoreArr | Group-Object | Sort-Object Name

Write-Host ""
Write-Host "============================================"
Write-Host "  RINHA BACKEND 2026 — BENCHMARK RESULTS"
Write-Host "============================================"
Write-Host "  Total requests : $TotalRequests"
Write-Host "  Concurrency    : $Concurrency VUs"
Write-Host "  Duration       : $([math]::Round($elapsed,1))s"
Write-Host "  RPS            : $rps req/s"
Write-Host "--------------------------------------------"
Write-Host "  LATENCY (ms)"
Write-Host "    min  : $min"
Write-Host "    avg  : $avg"
Write-Host "    p50  : $p50"
Write-Host "    p75  : $p75"
Write-Host "    p90  : $p90"
Write-Host "    p95  : $p95"
Write-Host "    p99  : $p99"
Write-Host "    max  : $max"
Write-Host "--------------------------------------------"
Write-Host "  FAILURES"
Write-Host "    count: $realFail / $TotalRequests"
Write-Host "    rate : $failPct%"
if ($realFail -gt 0) {
    $failures | Select-Object -First 3 | ForEach-Object { Write-Host "    err: $_" }
}
Write-Host "--------------------------------------------"
Write-Host "  SCORE DISTRIBUTION"
$scoreFreq | ForEach-Object { Write-Host "    score=$($_.Name) -> $($_.Count) reqs" }
Write-Host "============================================"

# Return as JSON for capture
@{
    total=$TotalRequests; concurrency=$Concurrency; duration_s=[math]::Round($elapsed,2)
    rps=$rps; min_ms=$min; avg_ms=$avg; p50=$p50; p75=$p75; p90=$p90; p95=$p95; p99=$p99; max_ms=$max
    failures=$realFail; failure_pct=$failPct
} | ConvertTo-Json
