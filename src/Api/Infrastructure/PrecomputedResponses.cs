using System.Text.Json;
using Api.Models;

namespace Api.Infrastructure;

/// <summary>
/// Pre-serialized JSON responses for all possible fraud scores (K=5 → 6 values).
/// Avoids any allocation on the hot path.
/// </summary>
public static class PrecomputedResponses
{
    private static readonly Dictionary<int, byte[]> Cache = new();

    static PrecomputedResponses()
    {
        // K=5 means fraud_score is always one of: 0/5, 1/5, 2/5, 3/5, 4/5, 5/5
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
    /// Returns the pre-serialized JSON bytes for the given fraud score.
    /// </summary>
    public static byte[] GetResponseBytes(float fraudScore)
    {
        int fraudCount = (int)MathF.Round(fraudScore * 5f);
        if (fraudCount < 0) fraudCount = 0;
        if (fraudCount > 5) fraudCount = 5;
        return Cache[fraudCount];
    }
}
