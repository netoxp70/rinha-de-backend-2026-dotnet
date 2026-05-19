using System.Text.Json.Serialization;
using Api.Models;

namespace Api;

[JsonSerializable(typeof(TransactionRequest))]
[JsonSerializable(typeof(FraudScoreResponse))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext
{
}
