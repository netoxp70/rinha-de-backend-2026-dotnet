using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shared;

namespace Api.Infrastructure;

public sealed class KnnEngine(IvfIndex index, int nprobe = Constants.DefaultNProbe, int k = Constants.K)
{
    private readonly IvfIndex _index = index;
    private readonly int _nprobe = nprobe;
    private readonly int _k = k;
    private readonly QueryCache _cache = new();

    // Borderline re-probe: when fraud_score is near the 0.6 threshold,
    // re-scan with more cells to improve accuracy on borderline cases.
    private readonly int _borderlineNprobe = int.TryParse(
            Environment.GetEnvironmentVariable("IVF_BORDERLINE_NPROBE"), out var bnp) && bnp > 0
            ? bnp : 32;
    private readonly bool _borderlineEnabled = Environment.GetEnvironmentVariable("IVF_BORDERLINE") != "0";

    // Q8 2-phase: how many Q8 candidates to gather before F32 rerank.
    // Q8_OVERFETCH=20 means scan for top-20 Q8 candidates, rerank with F32 for top-5.
    private readonly int _q8OverFetch = int.TryParse(
            Environment.GetEnvironmentVariable("Q8_OVERFETCH"), out var qof) && qof > 0
            ? qof : 20;
    private readonly bool _q8Enabled = Environment.GetEnvironmentVariable("Q8_SCAN") != "0";

    public bool IsReady => _index.VectorCount > 0;

    /// <summary>
    /// Performs KNN search and returns the fraud score (0.0, 0.2, 0.4, 0.6, 0.8, 1.0).
    /// Uses 2-phase Q8→F32 when Q8 data is available, pure F32 otherwise.
    /// Applies borderline re-probe near the decision boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float ComputeFraudScore(ref Vector14F queryVector)
    {
        // Zero-copy span over the struct's floats — no copy needed (struct is on caller's stack/heap).
        ReadOnlySpan<float> query = MemoryMarshal.CreateReadOnlySpan(ref queryVector.D0, Constants.PaddedDimensions);

        if (_cache.TryGet(query, out float cached))
            return cached;

        Span<int>   topKIdx  = stackalloc int[_k];
        Span<float> topKDist = stackalloc float[_k];
        float score = ScoreWithNprobe(query, _nprobe, topKIdx, topKDist);

        // Borderline re-probe: only when fraudCount is 2 or 3 (near threshold)
        // AND the distance margin between closest fraud and closest legit is tight.
        // This avoids the 16x cost on clear-cut cases.
        if (_borderlineEnabled && _borderlineNprobe > _nprobe)
        {
            int fraudCount = (int)MathF.Round(score * _k);
            if (fraudCount == 2 || fraudCount == 3)
            {
                float minFraud = float.MaxValue, minLegit = float.MaxValue;
                for (int i = 0; i < _k; i++)
                {
                    int idx = topKIdx[i];
                    if (idx < 0) continue;
                    float d = topKDist[i];
                    if (_index.GetLabel(idx) == 1) { if (d < minFraud) minFraud = d; }
                    else                           { if (d < minLegit) minLegit = d; }
                }
                float total = minFraud + minLegit;
                float margin = total > 0f ? MathF.Abs(minFraud - minLegit) / total : 0f;
                if (margin < 0.15f)
                    score = ScoreWithNprobe(query, _borderlineNprobe, topKIdx, topKDist);
            }
        }

        _cache.Set(query, score);
        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ScoreWithNprobe(ReadOnlySpan<float> query, int nprobe, Span<int> topKIndices, Span<float> topKDists)
    {
        // Cap nprobe to actual number of cells
        if (nprobe > _index.NList) nprobe = _index.NList;

        Span<int> cellIndices = stackalloc int[nprobe];
        _index.FindClosestCells(query, cellIndices, nprobe);

        if (_q8Enabled && _index.HasQ8)
        {
            // Phase 1: Q8 fast scan for overfetch candidates
            int overfetch = Math.Max(_k, _q8OverFetch);
            Span<sbyte> q8Query    = stackalloc sbyte[Constants.PaddedDimensions];
            Span<int>   q8Indices  = stackalloc int[overfetch];
            Span<long>  q8Dists    = stackalloc long[overfetch];

            _index.QuantizeQuery(query, q8Query);
            _index.ScanCellsQ8(q8Query, cellIndices, nprobe, q8Indices, q8Dists, overfetch);

            // Phase 2: F32 rerank of Q8 candidates
            topKDists.Fill(float.MaxValue);
            topKIndices.Fill(-1);
            for (int i = 0; i < overfetch; i++)
            {
                int idx = q8Indices[i];
                if (idx < 0) continue;
                var refVec = _index.GetVector(idx);
                float dist = SimdDistance.L2Squared(query, refVec);
                if (dist < topKDists[_k - 1])
                {
                    int insertPos = _k - 1;
                    while (insertPos > 0 && dist < topKDists[insertPos - 1])
                    {
                        topKDists[insertPos]   = topKDists[insertPos - 1];
                        topKIndices[insertPos] = topKIndices[insertPos - 1];
                        insertPos--;
                    }
                    topKDists[insertPos]   = dist;
                    topKIndices[insertPos] = idx;
                }
            }
        }
        else
        {
            _index.ScanCells(query, cellIndices, nprobe, topKIndices, topKDists, _k);
        }

        int fraudCount = 0;
        for (int i = 0; i < _k; i++)
        {
            if (topKIndices[i] >= 0 && _index.GetLabel(topKIndices[i]) == 1)
                fraudCount++;
        }

        return fraudCount / (float)_k;
    }
}
