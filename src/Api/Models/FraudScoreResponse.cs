using System.Text.Json.Serialization;

namespace Api.Models;

public sealed class FraudScoreResponse
{
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("fraud_score")]
    public float FraudScore { get; set; }
}
