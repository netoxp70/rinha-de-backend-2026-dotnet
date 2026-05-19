using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Shared;

const int ExpectedCount = 3_000_000;
const int NList = Constants.DefaultNList;
const int KMeansIterations = 5;
const int Seed = 42;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Preprocessor <references.json.gz> <output-dir>");
    return 1;
}

var inputPath = args[0];
var outputDir = args[1];
Directory.CreateDirectory(outputDir);

var sw = Stopwatch.StartNew();

// ─────────────────────────────────────────────────────────
// Step 1: Parse references.json.gz
// ─────────────────────────────────────────────────────────
Console.WriteLine($"[1/4] Parsing {inputPath}...");
var vectors = new float[ExpectedCount * Constants.PaddedDimensions];
var labels = new byte[ExpectedCount]; // 0=legit, 1=fraud
int count = 0;

using (var fs = File.OpenRead(inputPath))
using (var gz = new GZipStream(fs, CompressionMode.Decompress))
{
    var buffer = new byte[512 * 1024];
    using var ms = new MemoryStream();

    int read;
    while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
        ms.Write(buffer, 0, read);

    ms.Position = 0;
    var reader = new Utf8JsonReader(ms.ToArray());

    reader.Read(); // StartArray

    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
    {
        float[]? vec = null;
        string? label = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var prop = reader.GetString();
                reader.Read();

                if (prop == "vector")
                {
                    vec = new float[Constants.VectorDimensions];
                    int i = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (i < Constants.VectorDimensions)
                            vec[i++] = reader.GetSingle();
                    }
                }
                else if (prop == "label")
                {
                    label = reader.GetString();
                }
            }
        }

        if (vec != null && label != null)
        {
            int offset = count * Constants.PaddedDimensions;
            for (int i = 0; i < Constants.VectorDimensions; i++)
                vectors[offset + i] = vec[i];
            // Padding dimensions are already 0

            labels[count] = label == "fraud" ? (byte)1 : (byte)0;
            count++;

            if (count % 500_000 == 0)
                Console.WriteLine($"  Parsed {count:N0} vectors...");
        }
    }
}

Console.WriteLine($"  Total: {count:N0} vectors in {sw.Elapsed.TotalSeconds:F1}s");

// ─────────────────────────────────────────────────────────
// Step 2: k-means++ clustering
// ─────────────────────────────────────────────────────────
Console.WriteLine($"[2/4] Running k-means++ (nlist={NList}, iters={KMeansIterations})...");
sw.Restart();

var centroids = new float[NList * Constants.PaddedDimensions];
var assignments = new int[count];
var rng = new Random(Seed);

// Random initialization — O(nlist) vs O(N×nlist²) for k-means++.
// Sufficient quality when followed by k-means iterations.
{
    var picked = new HashSet<int>(NList);
    for (int c = 0; c < NList; c++)
    {
        int idx;
        do { idx = rng.Next(count); } while (!picked.Add(idx));
        Array.Copy(vectors, idx * Constants.PaddedDimensions, centroids, c * Constants.PaddedDimensions, Constants.PaddedDimensions);
    }
    Console.WriteLine($"  Random init done in {sw.Elapsed.TotalSeconds:F1}s");
}

// k-means iterations
for (int iter = 0; iter < KMeansIterations; iter++)
{
    var iterSw = Stopwatch.StartNew();

    // Assignment step
    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
    {
        int vOffset = i * Constants.PaddedDimensions;
        float bestDist = float.MaxValue;
        int bestCluster = 0;

        for (int c = 0; c < NList; c++)
        {
            int cOffset = c * Constants.PaddedDimensions;
            float dist = 0;
            for (int d = 0; d < Constants.VectorDimensions; d++)
            {
                float diff = vectors[vOffset + d] - centroids[cOffset + d];
                dist += diff * diff;
            }
            if (dist < bestDist)
            {
                bestDist = dist;
                bestCluster = c;
            }
        }

        assignments[i] = bestCluster;
    });

    // Update step
    var sums = new double[NList * Constants.PaddedDimensions];
    var counts = new int[NList];

    for (int i = 0; i < count; i++)
    {
        int cluster = assignments[i];
        counts[cluster]++;
        int vOffset = i * Constants.PaddedDimensions;
        int sOffset = cluster * Constants.PaddedDimensions;
        for (int d = 0; d < Constants.VectorDimensions; d++)
            sums[sOffset + d] += vectors[vOffset + d];
    }

    for (int c = 0; c < NList; c++)
    {
        if (counts[c] > 0)
        {
            int cOffset = c * Constants.PaddedDimensions;
            for (int d = 0; d < Constants.VectorDimensions; d++)
                centroids[cOffset + d] = (float)(sums[cOffset + d] / counts[c]);
        }
    }

    int maxCell = counts.Max();
    int minCell = counts.Where(x => x > 0).DefaultIfEmpty(0).Min();
    Console.WriteLine($"  Iter {iter + 1}/{KMeansIterations}: max_cell={maxCell:N0} min_cell={minCell:N0} ({iterSw.Elapsed.TotalSeconds:F1}s)");
}

Console.WriteLine($"  k-means done in {sw.Elapsed.TotalSeconds:F1}s");

// ─────────────────────────────────────────────────────────
// Step 3: Build IVF cell structure (SoA ordered by cluster)
// ─────────────────────────────────────────────────────────
Console.WriteLine("[3/4] Building IVF cell structure...");
sw.Restart();

var cellLengths = new int[NList];
for (int i = 0; i < count; i++)
    cellLengths[assignments[i]]++;

var cellOffsets = new int[NList];
for (int c = 1; c < NList; c++)
    cellOffsets[c] = cellOffsets[c - 1] + cellLengths[c - 1];

// Ordered indices per cluster
var orderedIndices = new int[count];
var cellFillPos = new int[NList];
for (int i = 0; i < count; i++)
{
    int cluster = assignments[i];
    int pos = cellOffsets[cluster] + cellFillPos[cluster];
    orderedIndices[pos] = i;
    cellFillPos[cluster]++;
}

Console.WriteLine($"  IVF structure built in {sw.Elapsed.TotalSeconds:F1}s");

// ─────────────────────────────────────────────────────────
// Step 4: Write binary files
// ─────────────────────────────────────────────────────────
Console.WriteLine("[4/5] Quantizing to Q8...");
sw.Restart();

// Compute per-dimension min and range (over VectorDimensions, not padded)
var dimMin   = new float[Constants.VectorDimensions];
var dimScale = new float[Constants.VectorDimensions]; // 255 / (max-min), 0 if constant

Array.Fill(dimMin, float.MaxValue);
var dimMax = new float[Constants.VectorDimensions];
Array.Fill(dimMax, float.MinValue);

for (int i = 0; i < count; i++)
{
    int offset = i * Constants.PaddedDimensions;
    for (int d = 0; d < Constants.VectorDimensions; d++)
    {
        float v = vectors[offset + d];
        if (v < dimMin[d]) dimMin[d] = v;
        if (v > dimMax[d]) dimMax[d] = v;
    }
}

for (int d = 0; d < Constants.VectorDimensions; d++)
{
    float range = dimMax[d] - dimMin[d];
    dimScale[d] = range > 1e-7f ? 255f / range : 0f;
}

// Quantize vectors: sbyte[count × PaddedDimensions] (padding dims = 0)
var q8Data = new sbyte[count * Constants.PaddedDimensions];
for (int i = 0; i < count; i++)
{
    int offset = i * Constants.PaddedDimensions;
    for (int d = 0; d < Constants.VectorDimensions; d++)
    {
        float v = vectors[offset + d];
        int q = (int)MathF.Round((v - dimMin[d]) * dimScale[d]);
        if (q < 0) q = 0;
        if (q > 255) q = 255;
        // Store as signed: shift [0,255] → [-128,127]
        q8Data[offset + d] = (sbyte)(q - 128);
    }
    // Padding dims remain 0
}

Console.WriteLine($"  Q8 quantization done in {sw.Elapsed.TotalSeconds:F1}s");

Console.WriteLine("[5/5] Writing binary files...");
sw.Restart();

// references_f32.bin: all vectors in original order (float32, padded to 16 dims)
using (var f = File.Create(Path.Combine(outputDir, "references_f32.bin")))
{
    var bytes = new byte[count * Constants.PaddedDimensions * sizeof(float)];
    Buffer.BlockCopy(vectors, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}
Console.WriteLine($"  references_f32.bin: {count * Constants.PaddedDimensions * 4 / 1048576} MB");

// references_q8.bin: quantized sbyte vectors (PaddedDimensions per vector)
unsafe
{
    var q8Bytes = new byte[count * Constants.PaddedDimensions];
    Buffer.BlockCopy(q8Data, 0, q8Bytes, 0, q8Bytes.Length);
    File.WriteAllBytes(Path.Combine(outputDir, "references_q8.bin"), q8Bytes);
}
Console.WriteLine($"  references_q8.bin: {count * Constants.PaddedDimensions / 1048576} MB");

// q8_params.bin: float32[VectorDimensions] min + float32[VectorDimensions] scale
using (var f = File.Create(Path.Combine(outputDir, "q8_params.bin")))
{
    var minBytes   = new byte[Constants.VectorDimensions * sizeof(float)];
    var scaleBytes = new byte[Constants.VectorDimensions * sizeof(float)];
    Buffer.BlockCopy(dimMin,   0, minBytes,   0, minBytes.Length);
    Buffer.BlockCopy(dimScale, 0, scaleBytes, 0, scaleBytes.Length);
    f.Write(minBytes);
    f.Write(scaleBytes);
}
Console.WriteLine($"  q8_params.bin: {Constants.VectorDimensions * 2 * 4} bytes");

// labels.bin
File.WriteAllBytes(Path.Combine(outputDir, "labels.bin"), labels.AsSpan(0, count).ToArray());
Console.WriteLine($"  labels.bin: {count} bytes");

// ivf_centroids.bin (float32, NList × PaddedDims)
using (var f = File.Create(Path.Combine(outputDir, "ivf_centroids.bin")))
{
    var bytes = new byte[NList * Constants.PaddedDimensions * sizeof(float)];
    Buffer.BlockCopy(centroids, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}

// ivf_assignments.bin (int32 per vector)
using (var f = File.Create(Path.Combine(outputDir, "ivf_assignments.bin")))
{
    var bytes = new byte[count * sizeof(int)];
    Buffer.BlockCopy(assignments, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}

// ivf_cell_offsets.bin (int32 per cell)
using (var f = File.Create(Path.Combine(outputDir, "ivf_cell_offsets.bin")))
{
    var bytes = new byte[NList * sizeof(int)];
    Buffer.BlockCopy(cellOffsets, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}

// ivf_cell_lengths.bin (int32 per cell)
using (var f = File.Create(Path.Combine(outputDir, "ivf_cell_lengths.bin")))
{
    var bytes = new byte[NList * sizeof(int)];
    Buffer.BlockCopy(cellLengths, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}

// ivf_ordered_indices.bin (int32 per vector)
using (var f = File.Create(Path.Combine(outputDir, "ivf_ordered_indices.bin")))
{
    var bytes = new byte[count * sizeof(int)];
    Buffer.BlockCopy(orderedIndices, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}

Console.WriteLine($"  All files written in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"\nDone! {count:N0} vectors processed.");
return 0;
