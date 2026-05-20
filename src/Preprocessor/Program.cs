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
Console.WriteLine($"[1/5] Parsing {inputPath}...");
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
Console.WriteLine($"[2/5] Running k-means (nlist={NList}, iters={KMeansIterations})...");
sw.Restart();

var centroids = new float[NList * Constants.PaddedDimensions];
var assignments = new int[count];
var rng = new Random(Seed);

// Random initialization
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
// Step 3: Build IVF ordering (cell-contiguous layout)
// ─────────────────────────────────────────────────────────
// Vectors are reordered so all vectors in cell c are contiguous.
// ivf_offsets.bin: (NList+1) fence-post int32 offsets (offset[c]..offset[c+1] is cell c).
// This eliminates the orderedIndices indirection at query time: scanning a cell
// is a sequential read of (offset[c+1]-offset[c]) rows starting at offset[c].
Console.WriteLine("[3/5] Building IVF cell ordering (contiguous layout)...");
sw.Restart();

var cellLengths = new int[NList];
for (int i = 0; i < count; i++)
    cellLengths[assignments[i]]++;

// Fence-post offsets: offset[0]=0, offset[c]=sum of lengths[0..c-1], offset[NList]=count
var cellOffsets = new int[NList + 1];
for (int c = 0; c < NList; c++)
    cellOffsets[c + 1] = cellOffsets[c] + cellLengths[c];

// Build cell-ordered permutation (maps new position → original index)
var orderedIndices = new int[count];
var cellFillPos = new int[NList];
for (int i = 0; i < count; i++)
{
    int cluster = assignments[i];
    int pos = cellOffsets[cluster] + cellFillPos[cluster];
    orderedIndices[pos] = i;
    cellFillPos[cluster]++;
}

Console.WriteLine($"  IVF ordering done in {sw.Elapsed.TotalSeconds:F1}s");

// ─────────────────────────────────────────────────────────
// Step 4: Quantize to Q8 (symmetric scale=127, same as reference)
// ─────────────────────────────────────────────────────────
// Symmetric: each dimension is normalized to [-1,1] already by the vectorizer,
// so multiply by 127 and round. range is intentionally clamped to [-128,127].
// This matches the reference project's Q8Scale=127 and means QuantizeQuery
// is just: q = clamp(round(v * 127), -128, 127) — no per-dim params needed.
Console.WriteLine("[4/5] Quantizing to Q8 (symmetric scale=127, cell-ordered)...");
sw.Restart();

// Cell-ordered Q8: vectors stored in IVF cell order, PaddedDimensions per row.
// Both the float and Q8 files share the same row ordering.
var q8DataOrdered  = new sbyte[count * Constants.PaddedDimensions];
var f32DataOrdered = new float[count * Constants.PaddedDimensions];
var labelsOrdered  = new byte[count];

for (int newPos = 0; newPos < count; newPos++)
{
    int origIdx  = orderedIndices[newPos];
    int srcOff   = origIdx  * Constants.PaddedDimensions;
    int dstOff   = newPos   * Constants.PaddedDimensions;

    labelsOrdered[newPos] = labels[origIdx];

    for (int d = 0; d < Constants.VectorDimensions; d++)
    {
        float v = vectors[srcOff + d];
        f32DataOrdered[dstOff + d] = v;

        int q = (int)MathF.Round(v * Constants.Q8Scale);
        if (q < -128) q = -128;
        if (q >  127) q =  127;
        q8DataOrdered[dstOff + d] = (sbyte)q;
    }
    // Padding dims remain 0
}

Console.WriteLine($"  Q8 quantization done in {sw.Elapsed.TotalSeconds:F1}s");

// ─────────────────────────────────────────────────────────
// Step 5: Write binary files
// ─────────────────────────────────────────────────────────
Console.WriteLine("[5/5] Writing binary files...");
sw.Restart();

// references_f32.bin: vectors in IVF cell order (float32, padded to 16 dims)
using (var f = File.Create(Path.Combine(outputDir, "references_f32.bin")))
{
    var bytes = new byte[count * Constants.PaddedDimensions * sizeof(float)];
    Buffer.BlockCopy(f32DataOrdered, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}
Console.WriteLine($"  references_f32.bin: {count * Constants.PaddedDimensions * 4 / 1048576} MB (cell-ordered)");

// references_q8.bin: cell-ordered Q8 sbyte vectors (PaddedDimensions per vector)
{
    var q8Bytes = new byte[count * Constants.PaddedDimensions];
    Buffer.BlockCopy(q8DataOrdered, 0, q8Bytes, 0, q8Bytes.Length);
    File.WriteAllBytes(Path.Combine(outputDir, "references_q8.bin"), q8Bytes);
}
Console.WriteLine($"  references_q8.bin: {count * Constants.PaddedDimensions / 1048576} MB (cell-ordered, symmetric)");

// labels.bin: in IVF cell order
File.WriteAllBytes(Path.Combine(outputDir, "labels.bin"), labelsOrdered);
Console.WriteLine($"  labels.bin: {count} bytes (cell-ordered)");

// ivf_centroids.bin (float32, NList × PaddedDims)
using (var f = File.Create(Path.Combine(outputDir, "ivf_centroids.bin")))
{
    var bytes = new byte[NList * Constants.PaddedDimensions * sizeof(float)];
    Buffer.BlockCopy(centroids, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}
Console.WriteLine($"  ivf_centroids.bin: {NList} centroids");

// ivf_offsets.bin: fence-post int32[NList+1] — offset[c]..offset[c+1] is cell c's range
using (var f = File.Create(Path.Combine(outputDir, "ivf_offsets.bin")))
{
    var bytes = new byte[(NList + 1) * sizeof(int)];
    Buffer.BlockCopy(cellOffsets, 0, bytes, 0, bytes.Length);
    f.Write(bytes);
}
Console.WriteLine($"  ivf_offsets.bin: {NList + 1} int32 fence-posts");

Console.WriteLine($"  All files written in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"\nDone! {count:N0} vectors processed.");
return 0;
