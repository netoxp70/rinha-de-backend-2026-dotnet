using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shared;

// ── Args ──────────────────────────────────────────────────────────────────────
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: RecallValidator <data-dir> [query-count=10000] [nprobe=1] [borderline-nprobe=32]");
    return 1;
}

string dataDir       = args[0];
int    queryCount    = args.Length > 1 ? int.Parse(args[1]) : 10_000;
int    nprobe        = args.Length > 2 ? int.Parse(args[2]) : 1;
int    borderlineNp  = args.Length > 3 ? int.Parse(args[3]) : 32;
const int K          = Constants.K;

Console.WriteLine($"Recall@{K} Validator — dataDir={dataDir}, queries={queryCount:N0}, nprobe={nprobe}, borderline={borderlineNp}");
Console.WriteLine();

// ── Load data ─────────────────────────────────────────────────────────────────
var sw = Stopwatch.StartNew();

var labelsBytes   = File.ReadAllBytes(Path.Combine(dataDir, "labels.bin"));
int vectorCount   = labelsBytes.Length;
Console.WriteLine($"Vectors: {vectorCount:N0}");

// Memory-map references_f32.bin
var vectorsPath = Path.Combine(dataDir, "references_f32.bin");
var fileSize    = new FileInfo(vectorsPath).Length;
using var mmf  = MemoryMappedFile.CreateFromFile(vectorsPath, FileMode.Open, null, fileSize, MemoryMappedFileAccess.Read);
using var acc  = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);
unsafe
{
    byte* rawPtr = null;
    acc.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
    float* vectorsPtr = (float*)(rawPtr + acc.PointerOffset);

    // Load IVF structure
    var centroidsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_centroids.bin"));
    var centroids      = new float[centroidsBytes.Length / sizeof(float)];
    Buffer.BlockCopy(centroidsBytes, 0, centroids, 0, centroidsBytes.Length);

    // Fence-post offsets: cellOffsets[c]..cellOffsets[c+1] is cell c's range.
    // Vectors/labels are already stored in IVF cell order — no orderedIndices needed.
    var offsetsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_offsets.bin"));
    var cellOffsets  = new int[offsetsBytes.Length / sizeof(int)];
    Buffer.BlockCopy(offsetsBytes, 0, cellOffsets, 0, offsetsBytes.Length);

    int nList = centroids.Length / Constants.PaddedDimensions;
    Console.WriteLine($"IVF: nlist={nList}, load time={sw.Elapsed.TotalSeconds:F1}s");

    // Cap query count to available vectors
    if (queryCount > vectorCount) queryCount = vectorCount;

    // ── Sampling: pick queryCount evenly-spaced query vectors ─────────────────
    // We use the last queryCount vectors as queries (they're "test" by convention).
    // This avoids using training vectors as test queries.
    int startIdx = Math.Max(0, vectorCount - queryCount);
    Console.WriteLine($"Queries: indices [{startIdx:N0}, {startIdx + queryCount - 1:N0}]");
    Console.WriteLine();

    // ── Brute-force ground truth ───────────────────────────────────────────────
    Console.WriteLine("Computing brute-force ground truth...");
    sw.Restart();

    // For each query: find exact top-K neighbors among ALL vectors
    // Skip the query vector itself (exact match → distance 0)
    var groundTruth = new int[queryCount * K]; // [qi * K + ki] = neighbor index

    Parallel.For(0, queryCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, qi =>
    {
        int queryIdx = startIdx + qi;
        float* qp = vectorsPtr + (long)queryIdx * Constants.PaddedDimensions;

        Span<int>   topIdx  = stackalloc int[K];
        Span<float> topDist = stackalloc float[K];
        topDist.Fill(float.MaxValue);
        topIdx.Fill(-1);

        for (int i = 0; i < vectorCount; i++)
        {
            if (i == queryIdx) continue; // skip self
            float* rp   = vectorsPtr + (long)i * Constants.PaddedDimensions;
            float  dist = L2Sq(qp, rp);

            if (dist < topDist[K - 1])
            {
                int pos = K - 1;
                while (pos > 0 && dist < topDist[pos - 1])
                {
                    topDist[pos] = topDist[pos - 1];
                    topIdx[pos]  = topIdx[pos - 1];
                    pos--;
                }
                topDist[pos] = dist;
                topIdx[pos]  = i;
            }
        }

        int baseOut = qi * K;
        for (int ki = 0; ki < K; ki++)
            groundTruth[baseOut + ki] = topIdx[ki];
    });

    Console.WriteLine($"  Ground truth done in {sw.Elapsed.TotalSeconds:F1}s");

    // ── IVF evaluation ─────────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine($"Evaluating IVF (nprobe={nprobe}, borderline={borderlineNp})...");
    sw.Restart();

    int matchesIvf    = 0;
    int matchesBorder = 0;
    long totalTimeIvfNs = 0;

    // Hoist stackalloc outside the loop (nprobe and borderlineNp are bounded)
    int maxNprobe = Math.Max(nprobe, Math.Min(borderlineNp, nList));
    int[] cellIdxArr   = new int[maxNprobe];
    float[] cellDistArr = new float[maxNprobe];
    int[] topKIdxArr   = new int[K];
    float[] topKDistArr = new float[K];
    int[] bCellIdxArr  = new int[maxNprobe];
    float[] bCellDistArr = new float[maxNprobe];

    for (int qi = 0; qi < queryCount; qi++)
    {
        int    queryIdx = startIdx + qi;
        float* qp       = vectorsPtr + (long)queryIdx * Constants.PaddedDimensions;

        var tStart = Stopwatch.GetTimestamp();

        // IVF search with nprobe
        Span<int>   cellIdx  = cellIdxArr.AsSpan(0, nprobe);
        Span<float> cellDist = cellDistArr.AsSpan(0, nprobe);
        cellDist.Fill(float.MaxValue);
        cellIdx.Fill(-1);

        // FindClosestCells
        fixed (float* cp = centroids)
        {
            for (int c = 0; c < nList; c++)
            {
                float* centroid = cp + (long)c * Constants.PaddedDimensions;
                float  dist     = L2Sq(qp, centroid);
                if (dist < cellDist[nprobe - 1])
                {
                    int pos = nprobe - 1;
                    while (pos > 0 && dist < cellDist[pos - 1])
                    {
                        cellDist[pos] = cellDist[pos - 1];
                        cellIdx[pos]  = cellIdx[pos - 1];
                        pos--;
                    }
                    cellDist[pos] = dist;
                    cellIdx[pos]  = c;
                }
            }
        }

        Span<int>   topKIdx  = topKIdxArr.AsSpan(0, K);
        Span<float> topKDist = topKDistArr.AsSpan(0, K);

        ScanCells(qp, cellIdx, nprobe, topKIdx, topKDist, K, cellOffsets, vectorsPtr, queryIdx);

        float score = FraudScore(topKIdx, K, labelsBytes);

        // Borderline re-probe
        int fc = (int)MathF.Round(score * K);
        if (fc == 2 || fc == 3)
        {
            int bnp = Math.Min(borderlineNp, nList);
            Span<int>   bCellIdx  = bCellIdxArr.AsSpan(0, bnp);
            Span<float> bCellDist = bCellDistArr.AsSpan(0, bnp);
            bCellDist.Fill(float.MaxValue);
            bCellIdx.Fill(-1);

            fixed (float* cp = centroids)
            {
                for (int c = 0; c < nList; c++)
                {
                    float* centroid = cp + (long)c * Constants.PaddedDimensions;
                    float  dist     = L2Sq(qp, centroid);
                    if (dist < bCellDist[bnp - 1])
                    {
                        int pos = bnp - 1;
                        while (pos > 0 && dist < bCellDist[pos - 1])
                        {
                            bCellDist[pos] = bCellDist[pos - 1];
                            bCellIdx[pos]  = bCellIdx[pos - 1];
                            pos--;
                        }
                        bCellDist[pos] = dist;
                        bCellIdx[pos]  = c;
                    }
                }
            }

            ScanCells(qp, bCellIdx, bnp, topKIdx, topKDist, K, cellOffsets, vectorsPtr, queryIdx);
        }

        totalTimeIvfNs += (long)((Stopwatch.GetTimestamp() - tStart) * 1_000_000_000.0 / Stopwatch.Frequency);

        // Count recall: how many of ground truth top-K are in IVF top-K?
        int baseGT = qi * K;
        for (int ki = 0; ki < K; ki++)
        {
            int gt = groundTruth[baseGT + ki];
            for (int ri = 0; ri < K; ri++)
            {
                if (topKIdx[ri] == gt) { matchesIvf++; break; }
            }
        }

        // With borderline (already applied above)
        for (int ki = 0; ki < K; ki++)
        {
            int gt = groundTruth[baseGT + ki];
            for (int ri = 0; ri < K; ri++)
            {
                if (topKIdx[ri] == gt) { matchesBorder++; break; }
            }
        }
    }

    double recallIvf = (double)matchesIvf / (queryCount * K);
    double recallBorder = (double)matchesBorder / (queryCount * K);
    double avgLatencyUs = totalTimeIvfNs / (double)queryCount / 1000.0;

    Console.WriteLine($"  Evaluation done in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine($"  Recall@{K} (IVF nprobe={nprobe}+borderline={borderlineNp}): {recallBorder:P2}");
    Console.WriteLine($"  Avg query latency (IVF): {avgLatencyUs:F1} µs");
    Console.WriteLine($"  Target: Recall@{K} ≥ 0.95");
    Console.WriteLine($"  Result: {(recallBorder >= 0.95 ? "✅ PASS" : "❌ FAIL — increase nprobe")}");
    Console.WriteLine("════════════════════════════════════════");

    acc.SafeMemoryMappedViewHandle.ReleasePointer();
}

return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static unsafe float L2Sq(float* a, float* b)
{
    float sum = 0;
    for (int i = 0; i < Constants.VectorDimensions; i++)
    {
        float d = a[i] - b[i];
        sum += d * d;
    }
    return sum;
}

static unsafe void ScanCells(
    float* qp,
    Span<int> cellIdx, int nprobe,
    Span<int> topKIdx, Span<float> topKDist, int k,
    int[] cellOffsets,
    float* vectorsPtr, int skipIdx)
{
    topKDist.Fill(float.MaxValue);
    topKIdx.Fill(-1);

    for (int p = 0; p < nprobe; p++)
    {
        int cell = cellIdx[p];
        if (cell < 0) continue;
        int start = cellOffsets[cell];
        int end   = cellOffsets[cell + 1];

        for (int vi = start; vi < end; vi++)
        {
            if (vi == skipIdx) continue;
            float* rp   = vectorsPtr + (long)vi * Constants.PaddedDimensions;
            float  dist = L2Sq(qp, rp);

            if (dist < topKDist[k - 1])
            {
                int pos = k - 1;
                while (pos > 0 && dist < topKDist[pos - 1])
                {
                    topKDist[pos] = topKDist[pos - 1];
                    topKIdx[pos]  = topKIdx[pos - 1];
                    pos--;
                }
                topKDist[pos] = dist;
                topKIdx[pos]  = vi;
            }
        }
    }
}

static float FraudScore(Span<int> indices, int k, byte[] labels)
{
    int fc = 0;
    for (int i = 0; i < k; i++)
        if (indices[i] >= 0 && labels[indices[i]] == 1) fc++;
    return fc / (float)k;
}
