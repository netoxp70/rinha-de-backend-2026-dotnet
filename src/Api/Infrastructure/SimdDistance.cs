using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Shared;

namespace Api.Infrastructure;

/// <summary>
/// SIMD-accelerated L2 squared distance for 14-dim vectors (padded to 16 floats).
/// AVX2 path processes 8 floats per cycle; SSE path processes 4; scalar fallback for ARM/other.
/// </summary>
public static class SimdDistance
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe float L2Squared(float* a, float* b)
    {
        if (Avx.IsSupported)
            return L2SquaredAvx2(a, b);
        if (Sse.IsSupported)
            return L2SquaredSse(a, b);
        return L2SquaredScalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float L2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        unsafe
        {
            fixed (float* pa = a, pb = b)
            {
                return L2Squared(pa, pb);
            }
        }
    }

    /// <summary>
    /// AVX2: two 256-bit loads cover 16 floats (our padded dimension).
    /// Padding dims are 0, so they contribute 0 to the sum — no masking needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float L2SquaredAvx2(float* a, float* b)
    {
        // Load first 8 floats (dims 0-7)
        var va0 = Avx.LoadVector256(a);
        var vb0 = Avx.LoadVector256(b);
        var diff0 = Avx.Subtract(va0, vb0);
        var sq0 = Avx.Multiply(diff0, diff0);

        // Load next 8 floats (dims 8-15, includes 2 zero-padded)
        var va1 = Avx.LoadVector256(a + 8);
        var vb1 = Avx.LoadVector256(b + 8);
        var diff1 = Avx.Subtract(va1, vb1);
        var sq1 = Avx.Multiply(diff1, diff1);

        // Horizontal sum: sq0 + sq1
        var sum = Avx.Add(sq0, sq1);

        // Reduce 8 floats → 1
        // hadd: [a0+a1, a2+a3, b0+b1, b2+b3, a4+a5, a6+a7, b4+b5, b6+b7]
        var hadd1 = Avx.HorizontalAdd(sum, sum);
        var hadd2 = Avx.HorizontalAdd(hadd1, hadd1);

        // Extract high 128 and add to low 128
        var lo = hadd2.GetLower();
        var hi = Avx2.ExtractVector128(hadd2, 1);
        var total = Sse.Add(lo, hi);

        return total.ToScalar();
    }

    /// <summary>
    /// SSE: four 128-bit loads cover 16 floats.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float L2SquaredSse(float* a, float* b)
    {
        var acc = Vector128<float>.Zero;

        for (int i = 0; i < 16; i += 4)
        {
            var va = Sse.LoadVector128(a + i);
            var vb = Sse.LoadVector128(b + i);
            var diff = Sse.Subtract(va, vb);
            acc = Sse.Add(acc, Sse.Multiply(diff, diff));
        }

        // Horizontal sum of 4 floats
        var shuf = Sse.MoveHighToLow(acc, acc);
        var sums = Sse.Add(acc, shuf);
        var final128 = Sse.AddScalar(sums, Sse.Shuffle(sums, sums, 0x01));
        return final128.ToScalar();
    }

    /// <summary>
    /// Scalar fallback (ARM64 / no SIMD).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float L2SquaredScalar(float* a, float* b)
    {
        float sum = 0;
        for (int i = 0; i < Constants.VectorDimensions; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }
}
