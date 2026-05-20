using System.Text.Json;
using Api.Models;

namespace Api.Infrastructure;

/// <summary>
/// Pre-serialized JSON responses for all possible fraud scores (K=5 → 6 values: 0,1,2,3,4,5 out of 5).
/// Direct array lookup (no Dictionary hash) — O(1) with no branching after clamp.
/// </summary>
public static class PrecomputedResponses
{
    private static readonly byte[][] Cache = new byte[6][];

    static PrecomputedResponses()
    {
        for (int fraudCount = 0; fraudCount <= 5; fraudCount++)
        {
            float score = fraudCount / 5f;
            var resp = new FraudScoreResponse
            {
                Approved = score < 0.6f,
                FraudScore = score
            };
            Cache[fraudCount] = JsonSerializer.SerializeToUtf8Bytes(resp, AppJsonContext.Default.FraudScoreResponse);
        }
    }

    /// <summary>
    /// Returns the pre-serialized JSON bytes for the given fraud score. Zero allocation.
    /// </summary>
    public static byte[] GetResponseBytes(float fraudScore)
    {
        int idx = (int)MathF.Round(fraudScore * 5f);
        if ((uint)idx > 5u) idx = idx < 0 ? 0 : 5;
        return Cache[idx];
    }

    /// <summary>Convert a fraud score (0.0..1.0 in 0.2 steps) to its 0..5 cache index.</summary>
    public static int IndexOf(float fraudScore)
    {
        int idx = (int)MathF.Round(fraudScore * 5f);
        if ((uint)idx > 5u) idx = idx < 0 ? 0 : 5;
        return idx;
    }

    /// <summary>Direct array lookup by 0..5 index — assumes the caller already validated the bounds.</summary>
    public static byte[] GetResponseBytesByIndex(int index) => Cache[index];
}
