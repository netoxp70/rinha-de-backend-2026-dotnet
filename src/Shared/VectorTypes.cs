using System.Runtime.InteropServices;

namespace Shared;

[StructLayout(LayoutKind.Sequential, Size = 16)]
public unsafe struct VectorQ8
{
    public fixed sbyte Data[16]; // 14 dims + 2 padding
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Vector14F
{
    public float D0, D1, D2, D3, D4, D5, D6, D7;
    public float D8, D9, D10, D11, D12, D13;
    public float Pad14, Pad15; // padding to 16 floats = 64 bytes

    public float this[int index]
    {
        get
        {
            unsafe
            {
                fixed (float* p = &D0)
                    return p[index];
            }
        }
        set
        {
            unsafe
            {
                fixed (float* p = &D0)
                    p[index] = value;
            }
        }
    }

    public static float DistanceSquared(ref Vector14F a, ref Vector14F b)
    {
        float sum = 0;
        unsafe
        {
            fixed (float* pa = &a.D0, pb = &b.D0)
            {
                for (int i = 0; i < Constants.VectorDimensions; i++)
                {
                    float diff = pa[i] - pb[i];
                    sum += diff * diff;
                }
            }
        }
        return sum;
    }
}
