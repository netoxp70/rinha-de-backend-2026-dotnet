namespace Shared;

public static class Constants
{
    public const int VectorDimensions = 14;
    public const int PaddedDimensions = 16; // Aligned to 16 for SIMD
    public const int K = 5;
    public const float FraudThreshold = 0.6f;

    // Normalization constants (from normalization.json)
    public const float MaxAmount = 10000f;
    public const float MaxInstallments = 12f;
    public const float AmountVsAvgRatio = 10f;
    public const float MaxMinutes = 1440f;
    public const float MaxKm = 1000f;
    public const float MaxTxCount24h = 20f;
    public const float MaxMerchantAvgAmount = 10000f;

    // Q8 quantization: symmetric scale (reference uses 127f)
    public const float Q8Scale = 127f;

    // IVF defaults
    public const int DefaultNList = 1024;
    public const int DefaultNProbe = 1;
    public const int DefaultRerank = 24;

    // Sentinel value for missing last_transaction
    public const float MissingSentinel = -1f;

    // File paths
    public const string ReferencesQ8Path  = "data/references_q8.bin";
    public const string ReferencesF32Path = "data/references_f32.bin";
    public const string LabelsPath        = "data/labels.bin";
    public const string IvfCentroidsPath  = "data/ivf_centroids.bin";
    public const string IvfOffsetsPath    = "data/ivf_offsets.bin";
}
