using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// ── Args ─────────────────────────────────────────────────────────────────────
string url         = args.Length > 0 ? args[0] : "http://localhost:9999/fraud-score";
int    totalReqs   = args.Length > 1 ? int.Parse(args[1]) : 10_000;
int    concurrency = args.Length > 2 ? int.Parse(args[2]) : 50;
int    warmupReqs  = args.Length > 3 ? int.Parse(args[3]) : 500;

// ── Payloads ──────────────────────────────────────────────────────────────────
string[] payloads =
[
    """{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}""",
    """{"id":"tx-F1","transaction":{"amount":4368.82,"installments":8,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-015","MERC-017","MERC-007"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}""",
    """{"id":"tx-L2","transaction":{"amount":120.00,"installments":1,"requested_at":"2026-04-01T10:00:00Z"},"customer":{"avg_amount":110.00,"tx_count_24h":1,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5812","avg_amount":100.00},"terminal":{"is_online":true,"card_present":true,"km_from_home":2.5},"last_transaction":null}""",
    """{"id":"tx-B1","transaction":{"amount":500.00,"installments":3,"requested_at":"2026-05-10T15:30:00Z"},"customer":{"avg_amount":450.00,"tx_count_24h":5,"known_merchants":["MERC-010","MERC-020"]},"merchant":{"id":"MERC-030","mcc":"5999","avg_amount":480.00},"terminal":{"is_online":true,"card_present":false,"km_from_home":15.0},"last_transaction":{"timestamp":"2026-05-10T14:00:00Z","km_from_current":5.0}}""",
];

// ── HttpClient with connection pool ─────────────────────────────────────────
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer     = concurrency + 4,
    PooledConnectionLifetime    = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    EnableMultipleHttp2Connections = false,
};
using var client = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };

// Pre-build StringContent objects (avoid per-request alloc)
var contents = payloads.Select(p => Encoding.UTF8.GetBytes(p)).ToArray();

Task<(bool ok, double ms)> SendOne(int idx)
{
    return Task.Run(async () =>
    {
        var body = new ByteArrayContent(contents[idx % contents.Length]);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var sw2 = Stopwatch.GetTimestamp();
        try
        {
            var resp = await client.PostAsync("", body);
            double ms = (Stopwatch.GetTimestamp() - sw2) * 1000.0 / Stopwatch.Frequency;
            return (resp.IsSuccessStatusCode, ms);
        }
        catch
        {
            double ms = (Stopwatch.GetTimestamp() - sw2) * 1000.0 / Stopwatch.Frequency;
            return (false, ms);
        }
    });
}

// ── Warmup ────────────────────────────────────────────────────────────────────
Console.Write($"Warming up ({warmupReqs} reqs, {concurrency} VUs)...");
var wTasks = new List<Task<(bool ok, double ms)>>(concurrency);
int wSent = 0;
while (wSent < warmupReqs)
{
    while (wTasks.Count < concurrency && wSent < warmupReqs)
    {
        wTasks.Add(SendOne(wSent++));
    }
    int done = Task.WaitAny(wTasks.ToArray());
    wTasks.RemoveAt(done);
}
await Task.WhenAll(wTasks);
Console.WriteLine(" done.");

// ── Benchmark ─────────────────────────────────────────────────────────────────
Console.WriteLine($"Benchmark: {totalReqs} reqs, {concurrency} VUs → {url}");

var latencies = new double[totalReqs];
var results   = new bool[totalReqs];
var tasks     = new List<Task<(bool ok, double ms)>>(concurrency);
var taskIdx   = new List<int>(concurrency);
int sent = 0, received = 0;

var swTotal = Stopwatch.StartNew();
while (sent < totalReqs || received < totalReqs)
{
    while (tasks.Count < concurrency && sent < totalReqs)
    {
        taskIdx.Add(sent);
        tasks.Add(SendOne(sent++));
    }
    if (tasks.Count == 0) break;
    int doneIdx = Task.WaitAny(tasks.ToArray());
    var (ok, ms) = await tasks[doneIdx];
    int reqIdx   = taskIdx[doneIdx];
    latencies[reqIdx] = ms;
    results[reqIdx]   = ok;
    tasks.RemoveAt(doneIdx);
    taskIdx.RemoveAt(doneIdx);
    received++;
}
swTotal.Stop();

// ── Stats ─────────────────────────────────────────────────────────────────────
Array.Sort(latencies);
double elapsed = swTotal.Elapsed.TotalSeconds;
double rps     = totalReqs / elapsed;
int    errors  = results.Count(r => !r);

double Pct(double p) => latencies[(int)(totalReqs * p)];

Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine("  C# HttpClient Benchmark — keep-alive connection pool");
Console.WriteLine("============================================================");
Console.WriteLine($"  total      : {totalReqs:N0}");
Console.WriteLine($"  concurrency: {concurrency} VUs");
Console.WriteLine($"  duration   : {elapsed:F1}s");
Console.WriteLine($"  RPS        : {rps:F0}");
Console.WriteLine("------------------------------------------------------------");
Console.WriteLine("  LATENCY (ms)");
Console.WriteLine($"    min   : {latencies[0]:F3}");
Console.WriteLine($"    p50   : {Pct(0.50):F3}");
Console.WriteLine($"    p75   : {Pct(0.75):F3}");
Console.WriteLine($"    p90   : {Pct(0.90):F3}");
Console.WriteLine($"    p95   : {Pct(0.95):F3}");
Console.WriteLine($"    p99   : {Pct(0.99):F3}");
Console.WriteLine($"    p99.9 : {Pct(0.999):F3}");
Console.WriteLine($"    max   : {latencies[^1]:F3}");
Console.WriteLine("------------------------------------------------------------");
Console.WriteLine($"  errors     : {errors} / {totalReqs}");
Console.WriteLine("============================================================");

// JSON output
var result = new
{
    total = totalReqs, concurrency, duration_s = Math.Round(elapsed, 2),
    rps = Math.Round(rps, 0),
    min_ms    = Math.Round(latencies[0], 3),
    p50       = Math.Round(Pct(0.50), 3),
    p75       = Math.Round(Pct(0.75), 3),
    p90       = Math.Round(Pct(0.90), 3),
    p95       = Math.Round(Pct(0.95), 3),
    p99       = Math.Round(Pct(0.99), 3),
    p999      = Math.Round(Pct(0.999), 3),
    max_ms    = Math.Round(latencies[^1], 3),
    errors
};
Console.WriteLine();
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
