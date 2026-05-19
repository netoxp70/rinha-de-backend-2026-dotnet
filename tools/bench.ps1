$payloads = @(
  '{"id":"tx-1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}',
  '{"id":"tx-2","transaction":{"amount":4368.82,"installments":8,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-015","MERC-017","MERC-007"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}'
)
$url = 'http://localhost:9999/fraud-score'
$totalRequests = 1000
$concurrency   = 50
$latencies = [System.Collections.Concurrent.ConcurrentBag[long]]::new()

# Warmup
1..20 | ForEach-Object {
  try { $null = Invoke-RestMethod $url -Method POST -ContentType 'application/json' -Body $payloads[$_ % 2] } catch {}
}
Write-Host 'Warmup done. Starting benchmark...'

$sw = [Diagnostics.Stopwatch]::StartNew()

1..$totalRequests | ForEach-Object -ThrottleLimit $concurrency -Parallel {
  $lat = $using:latencies
  $pay = $using:payloads
  $u   = $using:url
  try {
    $t = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-RestMethod $u -Method POST -ContentType 'application/json' -Body $pay[$_ % 2]
    $t.Stop()
    $lat.Add($t.ElapsedMilliseconds)
  } catch { }
}

$sw.Stop()
$sorted = $latencies | Sort-Object
$n  = $sorted.Count
$p50 = $sorted[[int]($n*0.50)]
$p95 = $sorted[[int]($n*0.95)]
$p99 = $sorted[[int]($n*0.99)]
$avg = [int](($sorted | Measure-Object -Average).Average)
$max = $sorted[-1]
$rps = [int]($n / $sw.Elapsed.TotalSeconds)

Write-Host "Results: n=$n rps=$rps avg=${avg}ms p50=${p50}ms p95=${p95}ms p99=${p99}ms max=${max}ms"
if ($p99 -lt 2000) { Write-Host "p99 target <2000ms: PASS" }
else { Write-Host "p99 target <2000ms: FAIL" }
