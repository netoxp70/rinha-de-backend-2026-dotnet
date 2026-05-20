# Fast HTTP benchmark — uses System.Net.Http.HttpClient (one shared instance per worker)
# to avoid the per-call overhead of Invoke-RestMethod. Far more accurate for sub-ms latencies.
param(
    [int]$TotalRequests = 5000,
    [int]$Concurrency   = 50,
    [string]$Url        = 'http://localhost:9999/fraud-score',
    [int]$Warmup        = 500
)

Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class FastBench
{
    public static (long[] latencies, int failures, double elapsedMs, int[] statuses) Run(
        string url, int total, int concurrency, string[] payloads, int warmup)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = concurrency * 2,
            EnableMultipleHttp2Connections = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        // Warmup — sequential, primes connections + JIT
        var contents = new ByteArrayContent[payloads.Length];
        for (int i = 0; i < payloads.Length; i++)
        {
            contents[i] = new ByteArrayContent(Encoding.UTF8.GetBytes(payloads[i]));
            contents[i].Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        for (int i = 0; i < warmup; i++)
        {
            using var c = new ByteArrayContent(Encoding.UTF8.GetBytes(payloads[i % payloads.Length]));
            c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var r = client.PostAsync(url, c).GetAwaiter().GetResult();
            _ = r.StatusCode;
        }

        var latencies = new long[total];
        var statuses  = new int[total];
        var failures  = 0;

        var swAll = Stopwatch.StartNew();
        var freq  = Stopwatch.Frequency;
        var nsPerTick = 1_000_000_000.0 / freq;

        var tasks = new Task[concurrency];
        int next  = -1;
        for (int w = 0; w < concurrency; w++)
        {
            tasks[w] = Task.Run(async () =>
            {
                while (true)
                {
                    int idx = Interlocked.Increment(ref next);
                    if (idx >= total) return;

                    var body = new ByteArrayContent(Encoding.UTF8.GetBytes(payloads[idx % payloads.Length]));
                    body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                    long t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        using var resp = await client.PostAsync(url, body).ConfigureAwait(false);
                        // Drain body so connection returns to pool
                        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        long t1 = Stopwatch.GetTimestamp();
                        latencies[idx] = (long)((t1 - t0) * nsPerTick);
                        statuses[idx]  = (int)resp.StatusCode;
                        if (!resp.IsSuccessStatusCode) Interlocked.Increment(ref failures);
                    }
                    catch
                    {
                        long t1 = Stopwatch.GetTimestamp();
                        latencies[idx] = (long)((t1 - t0) * nsPerTick);
                        statuses[idx]  = -1;
                        Interlocked.Increment(ref failures);
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        swAll.Stop();

        client.Dispose();
        handler.Dispose();

        return (latencies, failures, swAll.Elapsed.TotalMilliseconds, statuses);
    }
}
"@ -ReferencedAssemblies System.Net.Http,System.Net.Primitives,System.Threading,System.Threading.Tasks,System.Diagnostics.Process,System.Console,System.Text.Encoding,System.Runtime -ErrorAction Stop

$payloads = @(
    '{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}',
    '{"id":"tx-F1","transaction":{"amount":4368.82,"installments":8,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-015","MERC-017","MERC-007"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}',
    '{"id":"tx-L2","transaction":{"amount":120.00,"installments":1,"requested_at":"2026-04-01T10:00:00Z"},"customer":{"avg_amount":110.00,"tx_count_24h":1,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5812","avg_amount":100.00},"terminal":{"is_online":true,"card_present":true,"km_from_home":2.5},"last_transaction":null}',
    '{"id":"tx-B1","transaction":{"amount":500.00,"installments":3,"requested_at":"2026-05-10T15:30:00Z"},"customer":{"avg_amount":450.00,"tx_count_24h":5,"known_merchants":["MERC-010","MERC-020"]},"merchant":{"id":"MERC-030","mcc":"5999","avg_amount":480.00},"terminal":{"is_online":true,"card_present":false,"km_from_home":15.0},"last_transaction":{"timestamp":"2026-05-10T14:00:00Z","km_from_current":5.0}}'
)

Write-Host "Warmup ($Warmup reqs) + benchmark ($TotalRequests reqs / $Concurrency VUs)..."
$result = [FastBench]::Run($Url, $TotalRequests, $Concurrency, $payloads, $Warmup)

$latNs = $result.Item1
$failures = $result.Item2
$elapsedMs = $result.Item3
$statuses  = $result.Item4

# Convert to ms as double
$latMs = New-Object 'System.Collections.Generic.List[double]'
foreach ($v in $latNs) { $latMs.Add($v / 1e6) }
$arr = $latMs.ToArray()
[Array]::Sort($arr)

$n   = $arr.Length
$rps = [math]::Round($n * 1000.0 / $elapsedMs, 1)

function Pct([double[]]$s, [double]$p) {
    $i = [int][math]::Floor($s.Length * $p)
    if ($i -ge $s.Length) { $i = $s.Length - 1 }
    return $s[$i]
}

$min  = [math]::Round($arr[0], 3)
$avg  = [math]::Round((($arr | Measure-Object -Average).Average), 3)
$p50  = [math]::Round((Pct $arr 0.50), 3)
$p90  = [math]::Round((Pct $arr 0.90), 3)
$p95  = [math]::Round((Pct $arr 0.95), 3)
$p99  = [math]::Round((Pct $arr 0.99), 3)
$p999 = [math]::Round((Pct $arr 0.999), 3)
$max  = [math]::Round($arr[-1], 3)

# Status code histogram
$ok = 0; $err = 0
foreach ($s in $statuses) { if ($s -eq 200) { $ok++ } else { $err++ } }

Write-Host ''
Write-Host '============================================'
Write-Host '  FAST BENCHMARK — sub-ms accurate'
Write-Host '============================================'
Write-Host ("  total      : {0}" -f $n)
Write-Host ("  duration   : {0:N0} ms" -f $elapsedMs)
Write-Host ("  rps        : {0}" -f $rps)
Write-Host '--------------------------------------------'
Write-Host '  LATENCY (ms)'
Write-Host ("    min   : {0}" -f $min)
Write-Host ("    avg   : {0}" -f $avg)
Write-Host ("    p50   : {0}" -f $p50)
Write-Host ("    p90   : {0}" -f $p90)
Write-Host ("    p95   : {0}" -f $p95)
Write-Host ("    p99   : {0}" -f $p99)
Write-Host ("    p99.9 : {0}" -f $p999)
Write-Host ("    max   : {0}" -f $max)
Write-Host '--------------------------------------------'
Write-Host ("  ok={0}  err={1}  failures={2}" -f $ok, $err, $failures)
Write-Host '============================================'
