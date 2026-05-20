using System.Buffers;
using System.IO.Pipelines;
using System.Runtime;
using Api;
using Api.Infrastructure;
using Shared;

// ── GC & ThreadPool tuning ──────────────────────────────────────────────────
// SustainedLowLatency suppresses gen2 STW collections. Safe because scoring
// hot path is zero-alloc (stackalloc + pre-baked responses).
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

var tpMin = int.TryParse(Environment.GetEnvironmentVariable("TP_MIN_WORKERS"), out var _tpMin) && _tpMin > 0 ? _tpMin : 2;
var tpMax = int.TryParse(Environment.GetEnvironmentVariable("TP_MAX_WORKERS"), out var _tpMax) && _tpMax > 0 ? _tpMax : 4;
ThreadPool.SetMinThreads(workerThreads: tpMin, completionPortThreads: 1);
ThreadPool.SetMaxThreads(workerThreads: tpMax, completionPortThreads: 2);

// ── Load dataset ────────────────────────────────────────────────────────────
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "data";
var nprobe = int.TryParse(Environment.GetEnvironmentVariable("IVF_NPROBE"), out var np) ? np : Constants.DefaultNProbe;

Console.WriteLine($"Loading IVF index from '{dataDir}'...");
var index  = new IvfIndex(dataDir);
var engine = new KnnEngine(index, nprobe);
var bodyCache = new BodyCache();
Console.WriteLine($"Index loaded: {index.VectorCount:N0} vectors, {index.NList} cells, nprobe={nprobe}");

// ── Warmup: prefetch mmap pages + JIT warm-up ──────────────────────────────
if (Environment.GetEnvironmentVariable("WARMUP") != "0")
{
    var swWarm = System.Diagnostics.Stopwatch.StartNew();
    int warmupIters = int.TryParse(Environment.GetEnvironmentVariable("WARMUP_ITERS"), out var _wi) && _wi > 0 ? _wi : 200;
    var rng = new Random(20260505);
    float warmSink = 0f;

    // Representative fixed vectors: legit, fraud, borderline — primes JIT + page cache + QueryCache
    ReadOnlySpan<float[]> fixtures =
    [
        [0.0041f,  0.1667f, 0.05f,  0.7826f, 0.3333f, -1f,    -1f,    0.0292f, 0.15f,  0f, 1f, 0f, 0.15f,  0.006f ], // legit
        [0.9506f,  0.8333f, 1.0f,   0.2174f, 0.8333f, -1f,    -1f,    0.9523f, 1.0f,   0f, 1f, 1f, 0.75f,  0.0055f], // fraud
        [0.05f,    0.25f,   0.5f,   0.5f,    0.5f,    0.1f,   0.05f,  0.015f,  0.25f,  1f, 0f, 1f, 0.5f,   0.05f  ], // borderline
    ];

    for (int i = 0; i < warmupIters; i++)
    {
        var warmQ = new Shared.Vector14F();
        if (i % 4 < fixtures.Length)
        {
            var fx = fixtures[i % fixtures.Length];
            for (int d = 0; d < fx.Length; d++) warmQ[d] = fx[d];
        }
        else
        {
            for (int d = 0; d < Constants.VectorDimensions; d++)
                warmQ[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }
        warmSink += engine.ComputeFraudScore(ref warmQ);
    }
    swWarm.Stop();
    GC.KeepAlive(warmSink);
    Console.WriteLine($"Warmup: {warmupIters} iters in {swWarm.ElapsedMilliseconds}ms");
}

// ── Kestrel setup ───────────────────────────────────────────────────────────
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 4096;
    kestrel.Limits.MaxRequestHeadersTotalSize = 4096;
    kestrel.Limits.MaxConcurrentConnections = 512;
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);

    var udsPath = Environment.GetEnvironmentVariable("UDS_PATH");
    if (!string.IsNullOrEmpty(udsPath))
    {
        if (File.Exists(udsPath)) File.Delete(udsPath);
        kestrel.ListenUnixSocket(udsPath);
    }
    else
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 9999;
        kestrel.ListenAnyIP(port);
    }
});

var app = builder.Build();

// ── GET /ready ──────────────────────────────────────────────────────────────
app.MapGet("/ready", () => Results.Ok());

// ── POST /fraud-score ───────────────────────────────────────────────────────
// Sync fast-path: when the request body is already buffered in the pipe
// (typical for HTTP/1.1 keep-alive POST <1KB), we avoid building an async
// state machine entirely — no Task allocation, no thread hop.
// Slow-path (body not yet buffered) falls through to TryParseAsync.
app.MapPost("/fraud-score", (RequestDelegate)((HttpContext ctx) =>
{
    var pipe = ctx.Request.BodyReader;

    int rc = RequestParser.TryParseWithCache(pipe, bodyCache, out var query, out ulong bodyHash, out int cachedIdx);

    // rc == 2 — body cache hit: skip parse + KNN entirely
    if (rc == 2)
    {
        var bodyHit = PrecomputedResponses.GetResponseBytesByIndex(cachedIdx);
        return WriteResponse(ctx, bodyHit);
    }

    // rc == 1 — parsed ok: run KNN + populate body cache
    if (rc == 1)
    {
        float fraudScore = engine.ComputeFraudScore(ref query);
        int idx = PrecomputedResponses.IndexOf(fraudScore);
        bodyCache.Set(bodyHash, idx);
        var body = PrecomputedResponses.GetResponseBytesByIndex(idx);
        return WriteResponse(ctx, body);
    }

    // rc == 0 — bad JSON
    if (rc == 0)
    {
        ctx.Response.StatusCode = 422;
        return Task.CompletedTask;
    }

    // rc == -1 → body not yet buffered, fall back to async
    return SlowPathAsync(ctx);

    static Task WriteResponse(HttpContext ctx, byte[] body)
    {
        var resp = ctx.Response;
        resp.StatusCode    = 200;
        resp.ContentType   = "application/json";
        resp.ContentLength = body.Length;
        var writer = resp.BodyWriter;
        writer.Write(body.AsSpan());
        var flush = writer.FlushAsync(ctx.RequestAborted);
        return flush.IsCompletedSuccessfully ? Task.CompletedTask : flush.AsTask();
    }

    async Task SlowPathAsync(HttpContext c)
    {
        var (ok, q) = await RequestParser.TryParseAsync(c.Request.BodyReader, c.RequestAborted);
        if (!ok)
        {
            c.Response.StatusCode = 422;
            return;
        }
        float fraudScore = engine.ComputeFraudScore(ref q);
        var body = PrecomputedResponses.GetResponseBytes(fraudScore);
        c.Response.StatusCode    = 200;
        c.Response.ContentType   = "application/json";
        c.Response.ContentLength = body.Length;
        await c.Response.BodyWriter.WriteAsync(body);
    }
}));

// ── UDS chmod on startup ────────────────────────────────────────────────────
app.Lifetime.ApplicationStarted.Register(() =>
{
    var uds = Environment.GetEnvironmentVariable("UDS_PATH");
    if (!string.IsNullOrEmpty(uds) && File.Exists(uds))
    {
        try
        {
            File.SetUnixFileMode(uds,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch { /* best effort */ }
    }
    Console.WriteLine($"Ready. nprobe={nprobe}, vectors={index.VectorCount:N0}");
});

app.Run();
