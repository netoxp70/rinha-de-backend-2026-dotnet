using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Shared;

namespace Api.Infrastructure;

public sealed class IvfIndex : IDisposable
{
    private readonly MemoryMappedFile _vectorsMmf;
    private readonly MemoryMappedViewAccessor _vectorsAccessor;
    private readonly unsafe float* _vectorsPtr;

    // Q8 mmap (optional — null if file not present)
    private readonly MemoryMappedFile? _q8Mmf;
    private readonly MemoryMappedViewAccessor? _q8Accessor;
    private readonly unsafe sbyte* _q8Ptr;

    // Q8 quantization params: per-dim min and scale loaded from q8_params.bin
    private readonly float[] _q8Min;   // [VectorDimensions]
    private readonly float[] _q8Scale; // [VectorDimensions]
    private readonly bool _hasQ8;

    private readonly byte[] _labels;
    private readonly float[] _centroids; // NList × PaddedDims
    private readonly int[] _cellOffsets;
    private readonly int[] _cellLengths;
    private readonly int[] _orderedIndices;
    private readonly int _vectorCount;
    private readonly int _nList;

    public int VectorCount => _vectorCount;
    public int NList => _nList;

    public unsafe IvfIndex(string dataDir)
    {
        _nList = Constants.DefaultNList;

        // Load centroids
        var centroidsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_centroids.bin"));
        _centroids = new float[centroidsBytes.Length / sizeof(float)];
        Buffer.BlockCopy(centroidsBytes, 0, _centroids, 0, centroidsBytes.Length);

        // Load cell structure
        var offsetsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_cell_offsets.bin"));
        _cellOffsets = new int[offsetsBytes.Length / sizeof(int)];
        Buffer.BlockCopy(offsetsBytes, 0, _cellOffsets, 0, offsetsBytes.Length);

        var lengthsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_cell_lengths.bin"));
        _cellLengths = new int[lengthsBytes.Length / sizeof(int)];
        Buffer.BlockCopy(lengthsBytes, 0, _cellLengths, 0, lengthsBytes.Length);

        var indicesBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_ordered_indices.bin"));
        _orderedIndices = new int[indicesBytes.Length / sizeof(int)];
        Buffer.BlockCopy(indicesBytes, 0, _orderedIndices, 0, indicesBytes.Length);

        // Load labels
        _labels = File.ReadAllBytes(Path.Combine(dataDir, "labels.bin"));
        _vectorCount = _labels.Length;

        // Memory-map vectors file for shared page cache
        var vectorsPath = Path.Combine(dataDir, "references_f32.bin");
        var fileSize = new FileInfo(vectorsPath).Length;
        _vectorsMmf = MemoryMappedFile.CreateFromFile(vectorsPath, FileMode.Open, null, fileSize, MemoryMappedFileAccess.Read);
        _vectorsAccessor = _vectorsMmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        _vectorsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _vectorsPtr = (float*)(ptr + _vectorsAccessor.PointerOffset);

        // Load Q8 data if available
        _q8Min   = new float[Constants.VectorDimensions];
        _q8Scale = new float[Constants.VectorDimensions];
        _q8Ptr   = null;
        var q8Path     = Path.Combine(dataDir, "references_q8.bin");
        var q8ParamsPath = Path.Combine(dataDir, "q8_params.bin");
        if (File.Exists(q8Path) && File.Exists(q8ParamsPath))
        {
            var paramsBytes = File.ReadAllBytes(q8ParamsPath);
            int stride = Constants.VectorDimensions * sizeof(float);
            Buffer.BlockCopy(paramsBytes, 0,      _q8Min,   0, stride);
            Buffer.BlockCopy(paramsBytes, stride, _q8Scale, 0, stride);

            var q8FileSize = new FileInfo(q8Path).Length;
            _q8Mmf      = MemoryMappedFile.CreateFromFile(q8Path, FileMode.Open, null, q8FileSize, MemoryMappedFileAccess.Read);
            _q8Accessor = _q8Mmf.CreateViewAccessor(0, q8FileSize, MemoryMappedFileAccess.Read);
            byte* q8Ptr = null;
            _q8Accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref q8Ptr);
            _q8Ptr = (sbyte*)(q8Ptr + _q8Accessor.PointerOffset);
            _hasQ8 = true;
            Console.WriteLine($"Q8 index loaded: {q8FileSize / 1048576} MB");
        }
        else
        {
            _q8Mmf      = null;
            _q8Accessor = null;
            _hasQ8      = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ReadOnlySpan<float> GetVector(int index)
    {
        return new ReadOnlySpan<float>(_vectorsPtr + (long)index * Constants.PaddedDimensions, Constants.PaddedDimensions);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLabel(int index) => _labels[index];

    public ReadOnlySpan<float> GetCentroid(int cellIndex)
    {
        return _centroids.AsSpan(cellIndex * Constants.PaddedDimensions, Constants.PaddedDimensions);
    }

    /// <summary>
    /// Find the nprobe closest cells to the query vector.
    /// </summary>
    public void FindClosestCells(ReadOnlySpan<float> query, Span<int> cellIndices, int nprobe)
    {
        Span<float> distances = stackalloc float[nprobe];
        distances.Fill(float.MaxValue);
        cellIndices.Fill(-1);

        for (int c = 0; c < _nList; c++)
        {
            var centroid = GetCentroid(c);
            float dist = SimdDistance.L2Squared(query, centroid);

            // Insert into sorted top-nprobe
            if (dist < distances[nprobe - 1])
            {
                int insertPos = nprobe - 1;
                while (insertPos > 0 && dist < distances[insertPos - 1])
                {
                    distances[insertPos] = distances[insertPos - 1];
                    cellIndices[insertPos] = cellIndices[insertPos - 1];
                    insertPos--;
                }
                distances[insertPos] = dist;
                cellIndices[insertPos] = c;
            }
        }
    }

    /// <summary>
    /// Scan vectors within specified cells and return top-K nearest neighbors.
    /// </summary>
    public unsafe void ScanCells(ReadOnlySpan<float> query, ReadOnlySpan<int> cellIndices, int nprobe, Span<int> topKIndices, Span<float> topKDists, int k)
    {
        topKDists.Fill(float.MaxValue);
        topKIndices.Fill(-1);

        fixed (float* qp = query)
        {
            for (int p = 0; p < nprobe; p++)
            {
                int cell = cellIndices[p];
                if (cell < 0) continue;

                int offset = _cellOffsets[cell];
                int length = _cellLengths[cell];

                for (int i = 0; i < length; i++)
                {
                    int vectorIdx = _orderedIndices[offset + i];
                    float* refPtr = _vectorsPtr + (long)vectorIdx * Constants.PaddedDimensions;

                    // Prefetch next vector to hide L2/L3 latency
                    if (Sse.IsSupported && i + 4 < length)
                    {
                        int nextIdx = _orderedIndices[offset + i + 4];
                        Sse.Prefetch0(_vectorsPtr + (long)nextIdx * Constants.PaddedDimensions);
                    }

                    float dist = SimdDistance.L2Squared(qp, refPtr);

                    // Insert into sorted top-K (worst at position k-1)
                    if (dist < topKDists[k - 1])
                    {
                        int insertPos = k - 1;
                        while (insertPos > 0 && dist < topKDists[insertPos - 1])
                        {
                            topKDists[insertPos] = topKDists[insertPos - 1];
                            topKIndices[insertPos] = topKIndices[insertPos - 1];
                            insertPos--;
                        }
                        topKDists[insertPos] = dist;
                        topKIndices[insertPos] = vectorIdx;
                    }
                }
            }
        }
    }


    public bool HasQ8 => _hasQ8;

    /// <summary>
    /// Quantize a float query vector to Q8 using the stored per-dim params.
    /// Result is written into <paramref name="q8Query"/> (16 elements, padded to 0).
    /// </summary>
    public void QuantizeQuery(ReadOnlySpan<float> query, Span<sbyte> q8Query)
    {
        for (int d = 0; d < Constants.VectorDimensions; d++)
        {
            float scale = _q8Scale[d];
            int q = scale > 0f
                ? (int)MathF.Round((query[d] - _q8Min[d]) * scale)
                : 0;
            if (q < 0)   q = 0;
            if (q > 255) q = 255;
            q8Query[d] = (sbyte)(q - 128);
        }
        // Zero-pad remaining dims
        for (int d = Constants.VectorDimensions; d < Constants.PaddedDimensions; d++)
            q8Query[d] = 0;
    }

    /// <summary>
    /// Fast Q8 scan over specified cells: computes integer L2 squared distance.
    /// Returns top-<paramref name="k"/> candidate global indices (not sorted by distance).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe void ScanCellsQ8(
        ReadOnlySpan<sbyte> q8Query,
        ReadOnlySpan<int>   cellIndices,
        int                 nprobe,
        Span<int>           topKIndices,
        Span<long>          topKDists,
        int                 k)
    {
        topKDists.Fill(long.MaxValue);
        topKIndices.Fill(-1);

        fixed (sbyte* qp = q8Query)
        {
            for (int p = 0; p < nprobe; p++)
            {
                int cell = cellIndices[p];
                if (cell < 0) continue;

                int offset = _cellOffsets[cell];
                int length = _cellLengths[cell];

                for (int i = 0; i < length; i++)
                {
                    int vectorIdx = _orderedIndices[offset + i];
                    sbyte* refPtr = _q8Ptr + (long)vectorIdx * Constants.PaddedDimensions;

                    long dist = L2SquaredQ8(qp, refPtr);

                    if (dist < topKDists[k - 1])
                    {
                        int insertPos = k - 1;
                        while (insertPos > 0 && dist < topKDists[insertPos - 1])
                        {
                            topKDists[insertPos]   = topKDists[insertPos - 1];
                            topKIndices[insertPos] = topKIndices[insertPos - 1];
                            insertPos--;
                        }
                        topKDists[insertPos]   = dist;
                        topKIndices[insertPos] = vectorIdx;
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long L2SquaredQ8(sbyte* a, sbyte* b)
    {
        if (Sse2.IsSupported)
            return L2SquaredQ8Sse2(a, b);
        return L2SquaredQ8Scalar(a, b);
    }

    /// <summary>
    /// SSE2: load 16 sbytes each, unpack to int16, diff, square via MultiplyAddAdjacent, sum.
    /// Processes all 14 dims (+ 2 zero-padded) in ~4 SSE2 instructions — no scalar loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long L2SquaredQ8Sse2(sbyte* a, sbyte* b)
    {
        var va   = Sse2.LoadVector128(a);
        var vb   = Sse2.LoadVector128(b);
        var zero = Vector128<sbyte>.Zero;

        var vaLo = Sse2.UnpackLow(va, zero).AsInt16();
        var vbLo = Sse2.UnpackLow(vb, zero).AsInt16();
        var vaHi = Sse2.UnpackHigh(va, zero).AsInt16();
        var vbHi = Sse2.UnpackHigh(vb, zero).AsInt16();

        var diffLo = Sse2.Subtract(vaLo, vbLo);
        var diffHi = Sse2.Subtract(vaHi, vbHi);

        var sqLo = Sse2.MultiplyAddAdjacent(diffLo, diffLo);
        var sqHi = Sse2.MultiplyAddAdjacent(diffHi, diffHi);

        var sq8   = Sse2.Add(sqLo, sqHi);
        var shuf  = Sse2.Shuffle(sq8, 0b_10_11_00_01);
        var sum2  = Sse2.Add(sq8, shuf);
        var shuf2 = Sse2.Shuffle(sum2, 0b_00_00_10_10);
        var sum1  = Sse2.Add(sum2, shuf2);
        return Vector128.GetElement(sum1, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long L2SquaredQ8Scalar(sbyte* a, sbyte* b)
    {
        long sum = 0;
        for (int i = 0; i < Constants.VectorDimensions; i++)
        {
            int diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }

    public void Dispose()
    {
        _vectorsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _vectorsAccessor.Dispose();
        _vectorsMmf.Dispose();
        if (_q8Accessor is not null)
        {
            _q8Accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _q8Accessor.Dispose();
            _q8Mmf!.Dispose();
        }
    }
}
