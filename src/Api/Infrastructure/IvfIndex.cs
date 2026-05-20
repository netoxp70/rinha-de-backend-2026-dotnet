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
    private readonly bool _hasQ8;

    // Labels and IVF structure
    private readonly byte[] _labels;
    private readonly float[] _centroids; // NList × PaddedDims
    // Fence-post offsets: _cellOffsets[c]..._cellOffsets[c+1] is cell c's range.
    // Vectors/Q8/labels are stored in IVF cell order — no indirection needed.
    private readonly int[] _cellOffsets; // length NList+1
    private readonly int _vectorCount;
    private readonly int _nList;

    public int VectorCount => _vectorCount;
    public int NList => _nList;
    public bool HasQ8 => _hasQ8;

    public unsafe IvfIndex(string dataDir)
    {
        _nList = Constants.DefaultNList;

        // Load centroids
        var centroidsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_centroids.bin"));
        _centroids = new float[centroidsBytes.Length / sizeof(float)];
        Buffer.BlockCopy(centroidsBytes, 0, _centroids, 0, centroidsBytes.Length);

        // Load fence-post offsets (NList+1 int32 values)
        var offsetsBytes = File.ReadAllBytes(Path.Combine(dataDir, "ivf_offsets.bin"));
        _cellOffsets = new int[offsetsBytes.Length / sizeof(int)];
        Buffer.BlockCopy(offsetsBytes, 0, _cellOffsets, 0, offsetsBytes.Length);

        // Load labels (in IVF cell order)
        _labels = File.ReadAllBytes(Path.Combine(dataDir, "labels.bin"));
        _vectorCount = _labels.Length;

        // Memory-map vectors file (cell-ordered float32)
        var vectorsPath = Path.Combine(dataDir, "references_f32.bin");
        var fileSize = new FileInfo(vectorsPath).Length;
        _vectorsMmf = MemoryMappedFile.CreateFromFile(vectorsPath, FileMode.Open, null, fileSize, MemoryMappedFileAccess.Read);
        _vectorsAccessor = _vectorsMmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        _vectorsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _vectorsPtr = (float*)(ptr + _vectorsAccessor.PointerOffset);

        // Memory-map Q8 file (cell-ordered, symmetric scale=127)
        var q8Path = Path.Combine(dataDir, "references_q8.bin");
        if (File.Exists(q8Path))
        {
            var q8FileSize = new FileInfo(q8Path).Length;
            _q8Mmf      = MemoryMappedFile.CreateFromFile(q8Path, FileMode.Open, null, q8FileSize, MemoryMappedFileAccess.Read);
            _q8Accessor = _q8Mmf.CreateViewAccessor(0, q8FileSize, MemoryMappedFileAccess.Read);
            byte* q8Ptr = null;
            _q8Accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref q8Ptr);
            _q8Ptr = (sbyte*)(q8Ptr + _q8Accessor.PointerOffset);
            _hasQ8 = true;
            Console.WriteLine($"Q8 index loaded: {q8FileSize / 1048576} MB (symmetric)");
        }
        else
        {
            _q8Mmf = null; _q8Accessor = null; _q8Ptr = null; _hasQ8 = false;
        }
    }

    /// <summary>
    /// Pre-faults all mmap pages into the kernel page cache and advises huge pages.
    /// Call once at startup before serving requests.
    /// Returns total bytes touched.
    /// </summary>
    public unsafe long Prefetch()
    {
        long touched = 0;
        const int pageSize = 4096;

        // Touch every page of the float32 vectors mmap
        long vecBytes = (long)_vectorCount * Constants.PaddedDimensions * sizeof(float);
        byte* vp = (byte*)_vectorsPtr;
        for (long off = 0; off < vecBytes; off += pageSize)
        {
            _ = *(vp + off);
        }
        touched += vecBytes;

        // Touch Q8 pages
        if (_hasQ8)
        {
            long q8Bytes = (long)_vectorCount * Constants.PaddedDimensions;
            byte* q8p = (byte*)_q8Ptr;
            for (long off = 0; off < q8Bytes; off += pageSize)
            {
                _ = *(q8p + off);
            }
            touched += q8Bytes;
        }

        // madvise MADV_HUGEPAGE on Linux — reduces TLB pressure during cell scans
        if (OperatingSystem.IsLinux())
        {
            long vecBytes2 = (long)_vectorCount * Constants.PaddedDimensions * sizeof(float);
            madvise((nint)_vectorsPtr, (nuint)vecBytes2, 14); // MADV_HUGEPAGE = 14
            if (_hasQ8)
                madvise((nint)_q8Ptr, (nuint)((long)_vectorCount * Constants.PaddedDimensions), 14);
        }

        return touched;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = false)]
    private static extern int madvise(nint addr, nuint length, int advice);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ReadOnlySpan<float> GetVector(int index)
        => new(_vectorsPtr + (long)index * Constants.PaddedDimensions, Constants.PaddedDimensions);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLabel(int index) => _labels[index];

    /// <summary>Find the nprobe closest IVF cells to the query vector.</summary>
    public unsafe void FindClosestCells(ReadOnlySpan<float> query, Span<int> cellIndices, int nprobe)
    {
        Span<float> distances = stackalloc float[nprobe];
        distances.Fill(float.MaxValue);
        cellIndices.Fill(-1);

        fixed (float* qp = query)
        fixed (float* cp = _centroids)
        {
            int stride = Constants.PaddedDimensions;
            for (int c = 0; c < _nList; c++)
            {
                float* centPtr = cp + (long)c * stride;

                if (Sse.IsSupported && c + 4 < _nList)
                    Sse.Prefetch0(cp + (long)(c + 4) * stride);

                float dist = SimdDistance.L2Squared(qp, centPtr);

                if (dist < distances[nprobe - 1])
                {
                    int insertPos = nprobe - 1;
                    while (insertPos > 0 && dist < distances[insertPos - 1])
                    {
                        distances[insertPos]    = distances[insertPos - 1];
                        cellIndices[insertPos]  = cellIndices[insertPos - 1];
                        insertPos--;
                    }
                    distances[insertPos]   = dist;
                    cellIndices[insertPos] = c;
                }
            }
        }
    }

    /// <summary>
    /// F32 scan over selected cells. Vectors are contiguous in cell order — no indirection.
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

                int start  = _cellOffsets[cell];
                int end    = _cellOffsets[cell + 1];

                for (int i = start; i < end; i++)
                {
                    float* refPtr = _vectorsPtr + (long)i * Constants.PaddedDimensions;

                    if (Sse.IsSupported && i + 4 < end)
                        Sse.Prefetch0(_vectorsPtr + (long)(i + 4) * Constants.PaddedDimensions);

                    float dist = SimdDistance.L2Squared(qp, refPtr);

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
                        topKIndices[insertPos] = i;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Quantize query to Q8 using symmetric scale=127.
    /// No per-dim params needed — just multiply by 127 and clamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void QuantizeQuery(ReadOnlySpan<float> query, Span<sbyte> q8Query)
    {
        for (int d = 0; d < Constants.VectorDimensions; d++)
        {
            int q = (int)MathF.Round(query[d] * Constants.Q8Scale);
            if (q < -128) q = -128;
            if (q >  127) q =  127;
            q8Query[d] = (sbyte)q;
        }
        for (int d = Constants.VectorDimensions; d < Constants.PaddedDimensions; d++)
            q8Query[d] = 0;
    }

    /// <summary>
    /// Q8 scan over selected cells with AVX2 L2 distance.
    /// Vectors are contiguous in cell order — sequential memory access, no indirection.
    /// Optional class-aware early-stop: exits after earlyStopPct% of cells if top-K is unanimous.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public unsafe void ScanCellsQ8(
        ReadOnlySpan<sbyte> q8Query,
        ReadOnlySpan<int>   cellIndices,
        int                 nprobe,
        Span<int>           topKIndices,
        Span<int>           topKDists,
        int                 k,
        int                 earlyStopPct = 0)
    {
        topKDists.Fill(int.MaxValue);
        topKIndices.Fill(-1);

        fixed (sbyte* qp = q8Query)
        {
            int checkpoint = earlyStopPct > 0
                ? Math.Max(1, nprobe * earlyStopPct / 100)
                : nprobe;

            for (int p = 0; p < nprobe; p++)
            {
                int cell = cellIndices[p];
                if (cell < 0) continue;

                int start = _cellOffsets[cell];
                int end   = _cellOffsets[cell + 1];

                for (int i = start; i < end; i++)
                {
                    sbyte* refPtr = _q8Ptr + (long)i * Constants.PaddedDimensions;
                    int dist = L2SquaredQ8(qp, refPtr);

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
                        topKIndices[insertPos] = i;
                    }
                }

                // Class-aware early-stop: after checkpoint cells, check if all
                // top-K filled candidates share the same label (unanimous result).
                // If unanimous, further cells can't change the fraud/legit ratio.
                if (earlyStopPct > 0 && p + 1 == checkpoint)
                {
                    if (IsUnanimous(topKIndices, k))
                        break;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsUnanimous(Span<int> topKIndices, int k)
    {
        int filled = 0;
        byte firstLabel = 0xFF;
        for (int i = 0; i < k; i++)
        {
            int idx = topKIndices[i];
            if (idx < 0) continue;
            byte lbl = _labels[idx];
            if (firstLabel == 0xFF) { firstLabel = lbl; filled = 1; }
            else if (lbl != firstLabel) return false;
            else filled++;
        }
        return filled == k;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int L2SquaredQ8(sbyte* a, sbyte* b)
    {
        if (Avx2.IsSupported)  return L2SquaredQ8Avx2(a, b);
        if (Sse41.IsSupported) return L2SquaredQ8Sse41(a, b);
        return L2SquaredQ8Scalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int L2SquaredQ8Avx2(sbyte* a, sbyte* b)
    {
        var va = Sse2.LoadVector128(a);
        var vb = Sse2.LoadVector128(b);
        var vaW = Avx2.ConvertToVector256Int16(va);
        var vbW = Avx2.ConvertToVector256Int16(vb);
        var diff = Avx2.Subtract(vaW, vbW);
        var madd = Avx2.MultiplyAddAdjacent(diff, diff);
        return Vector256.Sum(madd);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int L2SquaredQ8Sse41(sbyte* a, sbyte* b)
    {
        var va = Sse2.LoadVector128(a);
        var vb = Sse2.LoadVector128(b);
        var vaLo = Sse41.ConvertToVector128Int16(va);
        var vbLo = Sse41.ConvertToVector128Int16(vb);
        var vaHi = Sse41.ConvertToVector128Int16(Sse2.ShiftRightLogical128BitLane(va, 8));
        var vbHi = Sse41.ConvertToVector128Int16(Sse2.ShiftRightLogical128BitLane(vb, 8));
        var sqLo = Sse2.MultiplyAddAdjacent(Sse2.Subtract(vaLo, vbLo), Sse2.Subtract(vaLo, vbLo));
        var sqHi = Sse2.MultiplyAddAdjacent(Sse2.Subtract(vaHi, vbHi), Sse2.Subtract(vaHi, vbHi));
        return Vector128.Sum(Sse2.Add(sqLo, sqHi));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int L2SquaredQ8Scalar(sbyte* a, sbyte* b)
    {
        int sum = 0;
        for (int i = 0; i < Constants.VectorDimensions; i++)
        {
            int diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }

    public unsafe void Dispose()
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
