using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Shared;

namespace Api.Infrastructure;

/// <summary>
/// SIMD-accelerated L2 squared distance for 14-dim vectors (padded to 16 floats).
/// AVX2 path: 2x 256-bit loads cover all 16 floats in 2 instructions — optimal for this fixed small dimension.
/// Both span and pointer overloads pin to the same AVX2 kernel via fixed pointer.
/// </summary>
public static class SimdDistance
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float L2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        unsafe
        {
            fixed (float* pa = a, pb = b)
                return L2Squared(pa, pb);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe float L2Squared(float* a, float* b)
    {
        if (Avx.IsSupported)
            return L2SquaredAvx(a, b);
        if (Sse.IsSupported)
            return L2SquaredSse(a, b);
        return L2SquaredScalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float L2SquaredAvx(float* a, float* b)
    {
        var diff0 = Avx.Subtract(Avx.LoadVector256(a),     Avx.LoadVector256(b));
        var diff1 = Avx.Subtract(Avx.LoadVector256(a + 8), Avx.LoadVector256(b + 8));
        var sq    = Avx.Add(Avx.Multiply(diff0, diff0), Avx.Multiply(diff1, diff1));
        var hadd1 = Avx.HorizontalAdd(sq, sq);
        var hadd2 = Avx.HorizontalAdd(hadd1, hadd1);
        return Sse.Add(hadd2.GetLower(), Avx2.ExtractVector128(hadd2, 1)).ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float L2SquaredSse(float* a, float* b)
    {
        var acc = Vector128<float>.Zero;
        for (int i = 0; i < 16; i += 4)
        {
            var diff = Sse.Subtract(Sse.LoadVector128(a + i), Sse.LoadVector128(b + i));
            acc = Sse.Add(acc, Sse.Multiply(diff, diff));
        }
        var shuf = Sse.MoveHighToLow(acc, acc);
        var sums = Sse.Add(acc, shuf);
        return Sse.AddScalar(sums, Sse.Shuffle(sums, sums, 0x01)).ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float L2SquaredScalar(float* a, float* b)
    {
        float sum = 0;
        for (int i = 0; i < Constants.VectorDimensions; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }
}
