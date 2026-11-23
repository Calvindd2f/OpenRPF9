using System;
using System.Runtime.InteropServices;

namespace OpenRPF9
{
    internal static class BcDecoders
    {
        private static readonly int[] Weight2 = { 0, 21, 43, 64 };
        private static readonly int[] Weight3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly int[] Weight4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
        private static readonly byte[] GammaLut = BuildGammaLut();

        private struct BitReader
        {
            public ulong Low;
            public ulong High;

            public int Read(int bits)
            {
                if (bits <= 0) return 0;
                ulong mask = (1UL << bits) - 1UL;
                ulong value = Low & mask;
                Low >>= bits;
                Low |= (High & mask) << (64 - bits);
                High >>= bits;
                return (int)value;
            }

            public int ReadReverse(int bits)
            {
                int value = Read(bits);
                int result = 0;
                while (bits-- > 0)
                {
                    result = (result << 1) | (value & 1);
                    value >>= 1;
                }
                return result;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [FieldOffset(0)] public uint U;
            [FieldOffset(0)] public float F;
        }

        private static readonly int[,] Bc6hBits = new int[4, 14]
        {
            { 10, 7, 11, 11, 11, 9, 8, 8, 8, 6, 10, 11, 12, 16 },
            {  5, 6,  5,  4,  4, 5, 6, 5, 5, 6, 10,  9,  8,  4 },
            {  5, 6,  4,  5,  4, 5, 5, 6, 5, 6, 10,  9,  8,  4 },
            {  5, 6,  4,  4,  5, 5, 5, 5, 6, 6, 10,  9,  8,  4 }
        };
    private static readonly byte[, ,] Bc6hPartitionRaw = new byte[32, 4, 4] {
        { {128, 0,   1, 1}, {0, 0, 1, 1}, {  0, 0, 1, 1}, {0, 0, 1, 129} },   
        { {128, 0,   0, 1}, {0, 0, 0, 1}, {  0, 0, 0, 1}, {0, 0, 0, 129} },   
        { {128, 1,   1, 1}, {0, 1, 1, 1}, {  0, 1, 1, 1}, {0, 1, 1, 129} },   
        { {128, 0,   0, 1}, {0, 0, 1, 1}, {  0, 0, 1, 1}, {0, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 1}, {  0, 0, 0, 1}, {0, 0, 1, 129} },   
        { {128, 0,   1, 1}, {0, 1, 1, 1}, {  0, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 1}, {0, 0, 1, 1}, {  0, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 1}, {  0, 0, 1, 1}, {0, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 0}, {  0, 0, 0, 1}, {0, 0, 1, 129} },   
        { {128, 0,   1, 1}, {0, 1, 1, 1}, {  1, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 1}, {  0, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 0}, {  0, 0, 0, 1}, {0, 1, 1, 129} },   
        { {128, 0,   0, 1}, {0, 1, 1, 1}, {  1, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 0}, {  1, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {1, 1, 1, 1}, {  1, 1, 1, 1}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {0, 0, 0, 0}, {  0, 0, 0, 0}, {1, 1, 1, 129} },   
        { {128, 0,   0, 0}, {1, 0, 0, 0}, {  1, 1, 1, 0}, {1, 1, 1, 129} },   
        { {128, 1, 129, 1}, {0, 0, 0, 1}, {  0, 0, 0, 0}, {0, 0, 0,   0} },   
        { {128, 0,   0, 0}, {0, 0, 0, 0}, {129, 0, 0, 0}, {1, 1, 1,   0} },   
        { {128, 1, 129, 1}, {0, 0, 1, 1}, {  0, 0, 0, 1}, {0, 0, 0,   0} },   
        { {128, 0, 129, 1}, {0, 0, 0, 1}, {  0, 0, 0, 0}, {0, 0, 0,   0} },   
        { {128, 0,   0, 0}, {1, 0, 0, 0}, {129, 1, 0, 0}, {1, 1, 1,   0} },   
        { {128, 0,   0, 0}, {0, 0, 0, 0}, {129, 0, 0, 0}, {1, 1, 0,   0} },   
        { {128, 1,   1, 1}, {0, 0, 1, 1}, {  0, 0, 1, 1}, {0, 0, 0, 129} },   
        { {128, 0, 129, 1}, {0, 0, 0, 1}, {  0, 0, 0, 1}, {0, 0, 0,   0} },   
        { {128, 0,   0, 0}, {1, 0, 0, 0}, {129, 0, 0, 0}, {1, 1, 0,   0} },   
        { {128, 1, 129, 0}, {0, 1, 1, 0}, {  0, 1, 1, 0}, {0, 1, 1,   0} },   
        { {128, 0, 129, 1}, {0, 1, 1, 0}, {  0, 1, 1, 0}, {1, 1, 0,   0} },   
        { {128, 0,   0, 1}, {0, 1, 1, 1}, {129, 1, 1, 0}, {1, 0, 0,   0} },   
        { {128, 0,   0, 0}, {1, 1, 1, 1}, {129, 1, 1, 1}, {0, 0, 0,   0} },   
        { {128, 1, 129, 1}, {0, 0, 0, 1}, {  1, 0, 0, 0}, {1, 1, 1,   0} },   
        { {128, 0, 129, 1}, {1, 0, 0, 1}, {  1, 0, 0, 1}, {1, 1, 0,   0} }    
    };
        private static readonly int[] Bc7ColorBits = { 4, 6, 5, 7, 5, 7, 7, 5 };
        private static readonly int[] Bc7AlphaBits = { 0, 0, 0, 0, 6, 8, 7, 5 };
    private static readonly byte[, , ,] Bc7PartitionRaw = new byte[2, 64, 4, 4] {
        {   
            { {128, 0,   1, 1}, {0, 0,   1, 1}, {  0, 0, 1, 1}, {0, 0, 1, 129} }, 
            { {128, 0,   0, 1}, {0, 0,   0, 1}, {  0, 0, 0, 1}, {0, 0, 0, 129} }, 
            { {128, 1,   1, 1}, {0, 1,   1, 1}, {  0, 1, 1, 1}, {0, 1, 1, 129} }, 
            { {128, 0,   0, 1}, {0, 0,   1, 1}, {  0, 0, 1, 1}, {0, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 1}, {  0, 0, 0, 1}, {0, 0, 1, 129} }, 
            { {128, 0,   1, 1}, {0, 1,   1, 1}, {  0, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 1}, {0, 0,   1, 1}, {  0, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 1}, {  0, 0, 1, 1}, {0, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 0}, {  0, 0, 0, 1}, {0, 0, 1, 129} }, 
            { {128, 0,   1, 1}, {0, 1,   1, 1}, {  1, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 1}, {  0, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 0}, {  0, 0, 0, 1}, {0, 1, 1, 129} }, 
            { {128, 0,   0, 1}, {0, 1,   1, 1}, {  1, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 0}, {  1, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {1, 1,   1, 1}, {  1, 1, 1, 1}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 0}, {  0, 0, 0, 0}, {1, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {1, 0,   0, 0}, {  1, 1, 1, 0}, {1, 1, 1, 129} }, 
            { {128, 1, 129, 1}, {0, 0,   0, 1}, {  0, 0, 0, 0}, {0, 0, 0,   0} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 0}, {129, 0, 0, 0}, {1, 1, 1,   0} }, 
            { {128, 1, 129, 1}, {0, 0,   1, 1}, {  0, 0, 0, 1}, {0, 0, 0,   0} }, 
            { {128, 0, 129, 1}, {0, 0,   0, 1}, {  0, 0, 0, 0}, {0, 0, 0,   0} }, 
            { {128, 0,   0, 0}, {1, 0,   0, 0}, {129, 1, 0, 0}, {1, 1, 1,   0} }, 
            { {128, 0,   0, 0}, {0, 0,   0, 0}, {129, 0, 0, 0}, {1, 1, 0,   0} }, 
            { {128, 1,   1, 1}, {0, 0,   1, 1}, {  0, 0, 1, 1}, {0, 0, 0, 129} }, 
            { {128, 0, 129, 1}, {0, 0,   0, 1}, {  0, 0, 0, 1}, {0, 0, 0,   0} }, 
            { {128, 0,   0, 0}, {1, 0,   0, 0}, {129, 0, 0, 0}, {1, 1, 0,   0} }, 
            { {128, 1, 129, 0}, {0, 1,   1, 0}, {  0, 1, 1, 0}, {0, 1, 1,   0} }, 
            { {128, 0, 129, 1}, {0, 1,   1, 0}, {  0, 1, 1, 0}, {1, 1, 0,   0} }, 
            { {128, 0,   0, 1}, {0, 1,   1, 1}, {129, 1, 1, 0}, {1, 0, 0,   0} }, 
            { {128, 0,   0, 0}, {1, 1,   1, 1}, {129, 1, 1, 1}, {0, 0, 0,   0} }, 
            { {128, 1, 129, 1}, {0, 0,   0, 1}, {  1, 0, 0, 0}, {1, 1, 1,   0} }, 
            { {128, 0, 129, 1}, {1, 0,   0, 1}, {  1, 0, 0, 1}, {1, 1, 0,   0} }, 
            { {128, 1,   0, 1}, {0, 1,   0, 1}, {  0, 1, 0, 1}, {0, 1, 0, 129} }, 
            { {128, 0,   0, 0}, {1, 1,   1, 1}, {  0, 0, 0, 0}, {1, 1, 1, 129} }, 
            { {128, 1,   0, 1}, {1, 0, 129, 0}, {  0, 1, 0, 1}, {1, 0, 1,   0} }, 
            { {128, 0,   1, 1}, {0, 0,   1, 1}, {129, 1, 0, 0}, {1, 1, 0,   0} }, 
            { {128, 0, 129, 1}, {1, 1,   0, 0}, {  0, 0, 1, 1}, {1, 1, 0,   0} }, 
            { {128, 1,   0, 1}, {0, 1,   0, 1}, {129, 0, 1, 0}, {1, 0, 1,   0} }, 
            { {128, 1,   1, 0}, {1, 0,   0, 1}, {  0, 1, 1, 0}, {1, 0, 0, 129} }, 
            { {128, 1,   0, 1}, {1, 0,   1, 0}, {  1, 0, 1, 0}, {0, 1, 0, 129} }, 
            { {128, 1, 129, 1}, {0, 0,   1, 1}, {  1, 1, 0, 0}, {1, 1, 1,   0} }, 
            { {128, 0,   0, 1}, {0, 0,   1, 1}, {129, 1, 0, 0}, {1, 0, 0,   0} }, 
            { {128, 0, 129, 1}, {0, 0,   1, 0}, {  0, 1, 0, 0}, {1, 1, 0,   0} }, 
            { {128, 0, 129, 1}, {1, 0,   1, 1}, {  1, 1, 0, 1}, {1, 1, 0,   0} }, 
            { {128, 1, 129, 0}, {1, 0,   0, 1}, {  1, 0, 0, 1}, {0, 1, 1,   0} }, 
            { {128, 0,   1, 1}, {1, 1,   0, 0}, {  1, 1, 0, 0}, {0, 0, 1, 129} }, 
            { {128, 1,   1, 0}, {0, 1,   1, 0}, {  1, 0, 0, 1}, {1, 0, 0, 129} }, 
            { {128, 0,   0, 0}, {0, 1, 129, 0}, {  0, 1, 1, 0}, {0, 0, 0,   0} }, 
            { {128, 1,   0, 0}, {1, 1, 129, 0}, {  0, 1, 0, 0}, {0, 0, 0,   0} }, 
            { {128, 0, 129, 0}, {0, 1,   1, 1}, {  0, 0, 1, 0}, {0, 0, 0,   0} }, 
            { {128, 0,   0, 0}, {0, 0, 129, 0}, {  0, 1, 1, 1}, {0, 0, 1,   0} }, 
            { {128, 0,   0, 0}, {0, 1,   0, 0}, {129, 1, 1, 0}, {0, 1, 0,   0} }, 
            { {128, 1,   1, 0}, {1, 1,   0, 0}, {  1, 0, 0, 1}, {0, 0, 1, 129} }, 
            { {128, 0,   1, 1}, {0, 1,   1, 0}, {  1, 1, 0, 0}, {1, 0, 0, 129} }, 
            { {128, 1, 129, 0}, {0, 0,   1, 1}, {  1, 0, 0, 1}, {1, 1, 0,   0} }, 
            { {128, 0, 129, 1}, {1, 0,   0, 1}, {  1, 1, 0, 0}, {0, 1, 1,   0} }, 
            { {128, 1,   1, 0}, {1, 1,   0, 0}, {  1, 1, 0, 0}, {1, 0, 0, 129} }, 
            { {128, 1,   1, 0}, {0, 0,   1, 1}, {  0, 0, 1, 1}, {1, 0, 0, 129} }, 
            { {128, 1,   1, 1}, {1, 1,   1, 0}, {  1, 0, 0, 0}, {0, 0, 0, 129} }, 
            { {128, 0,   0, 1}, {1, 0,   0, 0}, {  1, 1, 1, 0}, {0, 1, 1, 129} }, 
            { {128, 0,   0, 0}, {1, 1,   1, 1}, {  0, 0, 1, 1}, {0, 0, 1, 129} }, 
            { {128, 0, 129, 1}, {0, 0,   1, 1}, {  1, 1, 1, 1}, {0, 0, 0,   0} }, 
            { {128, 0, 129, 0}, {0, 0,   1, 0}, {  1, 1, 1, 0}, {1, 1, 1,   0} }, 
            { {128, 1,   0, 0}, {0, 1,   0, 0}, {  0, 1, 1, 1}, {0, 1, 1, 129} }  
        },
        {   
            { {128, 0, 1, 129}, {0,   0,   1, 1}, {  0,   2,   2, 1}, {  2,   2, 2, 130} }, 
            { {128, 0, 0, 129}, {0,   0,   1, 1}, {130,   2,   1, 1}, {  2,   2, 2,   1} }, 
            { {128, 0, 0,   0}, {2,   0,   0, 1}, {130,   2,   1, 1}, {  2,   2, 1, 129} }, 
            { {128, 2, 2, 130}, {0,   0,   2, 2}, {  0,   0,   1, 1}, {  0,   1, 1, 129} }, 
            { {128, 0, 0,   0}, {0,   0,   0, 0}, {129,   1,   2, 2}, {  1,   1, 2, 130} }, 
            { {128, 0, 1, 129}, {0,   0,   1, 1}, {  0,   0,   2, 2}, {  0,   0, 2, 130} }, 
            { {128, 0, 2, 130}, {0,   0,   2, 2}, {  1,   1,   1, 1}, {  1,   1, 1, 129} }, 
            { {128, 0, 1,   1}, {0,   0,   1, 1}, {130,   2,   1, 1}, {  2,   2, 1, 129} }, 
            { {128, 0, 0,   0}, {0,   0,   0, 0}, {129,   1,   1, 1}, {  2,   2, 2, 130} }, 
            { {128, 0, 0,   0}, {1,   1,   1, 1}, {129,   1,   1, 1}, {  2,   2, 2, 130} }, 
            { {128, 0, 0,   0}, {1,   1, 129, 1}, {  2,   2,   2, 2}, {  2,   2, 2, 130} }, 
            { {128, 0, 1,   2}, {0,   0, 129, 2}, {  0,   0,   1, 2}, {  0,   0, 1, 130} }, 
            { {128, 1, 1,   2}, {0,   1, 129, 2}, {  0,   1,   1, 2}, {  0,   1, 1, 130} }, 
            { {128, 1, 2,   2}, {0, 129,   2, 2}, {  0,   1,   2, 2}, {  0,   1, 2, 130} }, 
            { {128, 0, 1, 129}, {0,   1,   1, 2}, {  1,   1,   2, 2}, {  1,   2, 2, 130} }, 
            { {128, 0, 1, 129}, {2,   0,   0, 1}, {130,   2,   0, 0}, {  2,   2, 2,   0} }, 
            { {128, 0, 0, 129}, {0,   0,   1, 1}, {  0,   1,   1, 2}, {  1,   1, 2, 130} }, 
            { {128, 1, 1, 129}, {0,   0,   1, 1}, {130,   0,   0, 1}, {  2,   2, 0,   0} }, 
            { {128, 0, 0,   0}, {1,   1,   2, 2}, {129,   1,   2, 2}, {  1,   1, 2, 130} }, 
            { {128, 0, 2, 130}, {0,   0,   2, 2}, {  0,   0,   2, 2}, {  1,   1, 1, 129} }, 
            { {128, 1, 1, 129}, {0,   1,   1, 1}, {  0,   2,   2, 2}, {  0,   2, 2, 130} }, 
            { {128, 0, 0, 129}, {0,   0,   0, 1}, {130,   2,   2, 1}, {  2,   2, 2,   1} }, 
            { {128, 0, 0,   0}, {0,   0, 129, 1}, {  0,   1,   2, 2}, {  0,   1, 2, 130} }, 
            { {128, 0, 0,   0}, {1,   1,   0, 0}, {130,   2, 129, 0}, {  2,   2, 1,   0} }, 
            { {128, 1, 2, 130}, {0, 129,   2, 2}, {  0,   0,   1, 1}, {  0,   0, 0,   0} }, 
            { {128, 0, 1,   2}, {0,   0,   1, 2}, {129,   1,   2, 2}, {  2,   2, 2, 130} }, 
            { {128, 1, 1,   0}, {1,   2, 130, 1}, {129,   2,   2, 1}, {  0,   1, 1,   0} }, 
            { {128, 0, 0,   0}, {0,   1, 129, 0}, {  1,   2, 130, 1}, {  1,   2, 2,   1} }, 
            { {128, 0, 2,   2}, {1,   1,   0, 2}, {129,   1,   0, 2}, {  0,   0, 2, 130} }, 
            { {128, 1, 1,   0}, {0, 129,   1, 0}, {  2,   0,   0, 2}, {  2,   2, 2, 130} }, 
            { {128, 0, 1,   1}, {0,   1,   2, 2}, {  0,   1, 130, 2}, {  0,   0, 1, 129} }, 
            { {128, 0, 0,   0}, {2,   0,   0, 0}, {130,   2,   1, 1}, {  2,   2, 2, 129} }, 
            { {128, 0, 0,   0}, {0,   0,   0, 2}, {129,   1,   2, 2}, {  1,   2, 2, 130} }, 
            { {128, 2, 2, 130}, {0,   0,   2, 2}, {  0,   0,   1, 2}, {  0,   0, 1, 129} }, 
            { {128, 0, 1, 129}, {0,   0,   1, 2}, {  0,   0,   2, 2}, {  0,   2, 2, 130} }, 
            { {128, 1, 2,   0}, {0, 129,   2, 0}, {  0,   1, 130, 0}, {  0,   1, 2,   0} }, 
            { {128, 0, 0,   0}, {1,   1, 129, 1}, {  2,   2, 130, 2}, {  0,   0, 0,   0} }, 
            { {128, 1, 2,   0}, {1,   2,   0, 1}, {130,   0, 129, 2}, {  0,   1, 2,   0} }, 
            { {128, 1, 2,   0}, {2,   0,   1, 2}, {129, 130,   0, 1}, {  0,   1, 2,   0} }, 
            { {128, 0, 1,   1}, {2,   2,   0, 0}, {  1,   1, 130, 2}, {  0,   0, 1, 129} }, 
            { {128, 0, 1,   1}, {1,   1, 130, 2}, {  2,   2,   0, 0}, {  0,   0, 1, 129} }, 
            { {128, 1, 0, 129}, {0,   1,   0, 1}, {  2,   2,   2, 2}, {  2,   2, 2, 130} }, 
            { {128, 0, 0,   0}, {0,   0,   0, 0}, {130,   1,   2, 1}, {  2,   1, 2, 129} }, 
            { {128, 0, 2,   2}, {1, 129,   2, 2}, {  0,   0,   2, 2}, {  1,   1, 2, 130} }, 
            { {128, 0, 2, 130}, {0,   0,   1, 1}, {  0,   0,   2, 2}, {  0,   0, 1, 129} }, 
            { {128, 2, 2,   0}, {1,   2, 130, 1}, {  0,   2,   2, 0}, {  1,   2, 2, 129} }, 
            { {128, 1, 0,   1}, {2,   2, 130, 2}, {  2,   2,   2, 2}, {  0,   1, 0, 129} }, 
            { {128, 0, 0,   0}, {2,   1,   2, 1}, {130,   1,   2, 1}, {  2,   1, 2, 129} }, 
            { {128, 1, 0, 129}, {0,   1,   0, 1}, {  0,   1,   0, 1}, {  2,   2, 2, 130} }, 
            { {128, 2, 2, 130}, {0,   1,   1, 1}, {  0,   2,   2, 2}, {  0,   1, 1, 129} }, 
            { {128, 0, 0,   2}, {1, 129,   1, 2}, {  0,   0,   0, 2}, {  1,   1, 1, 130} }, 
            { {128, 0, 0,   0}, {2, 129,   1, 2}, {  2,   1,   1, 2}, {  2,   1, 1, 130} }, 
            { {128, 2, 2,   2}, {0, 129,   1, 1}, {  0,   1,   1, 1}, {  0,   2, 2, 130} }, 
            { {128, 0, 0,   2}, {1,   1,   1, 2}, {129,   1,   1, 2}, {  0,   0, 0, 130} }, 
            { {128, 1, 1,   0}, {0, 129,   1, 0}, {  0,   1,   1, 0}, {  2,   2, 2, 130} }, 
            { {128, 0, 0,   0}, {0,   0,   0, 0}, {  2,   1, 129, 2}, {  2,   1, 1, 130} }, 
            { {128, 1, 1,   0}, {0, 129,   1, 0}, {  2,   2,   2, 2}, {  2,   2, 2, 130} }, 
            { {128, 0, 2,   2}, {0,   0,   1, 1}, {  0,   0, 129, 1}, {  0,   0, 2, 130} }, 
            { {128, 0, 2,   2}, {1,   1,   2, 2}, {129,   1,   2, 2}, {  0,   0, 2, 130} }, 
            { {128, 0, 0,   0}, {0,   0,   0, 0}, {  0,   0,   0, 0}, {  2, 129, 1, 130} }, 
            { {128, 0, 0, 130}, {0,   0,   0, 1}, {  0,   0,   0, 2}, {  0,   0, 0, 129} }, 
            { {128, 2, 2,   2}, {1,   2,   2, 2}, {  0,   2,   2, 2}, {129,   2, 2, 130} }, 
            { {128, 1, 0, 129}, {2,   2,   2, 2}, {  2,   2,   2, 2}, {  2,   2, 2, 130} }, 
            { {128, 1, 1, 129}, {2,   0,   1, 1}, {130,   2,   0, 1}, {  2,   2, 2,   0} }  
        }
    };
        public static byte[] DecodeBc6h(byte[] data, int offset, int width, int height, bool signed)
        {
            byte[] rgba = new byte[width * height * 4];
            int p = offset;
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 4)
                {
                    if (p + 16 > data.Length) return rgba;
                    DecodeBc6hBlock(data, p, rgba, width, height, bx, by, signed);
                    p += 16;
                }
            }
            return rgba;
        }

        private static void DecodeBc6hBlock(byte[] data, int offset, byte[] rgba, int width, int height, int bx, int by, bool signed)
        {
            BitReader br = new BitReader();
            br.Low = BitConverter.ToUInt64(data, offset);
            br.High = BitConverter.ToUInt64(data, offset + 8);

            int[] r = new int[4];
            int[] g = new int[4];
            int[] b = new int[4];
            int partition = 0;
            int mode = br.Read(2);
            if (mode > 1) mode |= br.Read(3) << 2;
    switch (mode) {
        
        case 0x0: {
            g[2] |= br.Read(1) << 4;       
            b[2] |= br.Read(1) << 4;       
            b[3] |= br.Read(1) << 4;       
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(5);        
            g[3] |= br.Read(1) << 4;       
            g[2] |= br.Read(4);        
            g[1] |= br.Read(5);        
            b[3] |= br.Read(1);            
            g[3] |= br.Read(4);        
            b[1] |= br.Read(5);        
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(4);        
            r[2] |= br.Read(5);        
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(5);        
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 0;
        } break;

        
        case 0x1: {
            g[2] |= br.Read(1) << 5;       
            g[3] |= br.Read(1) << 4;       
            g[3] |= br.Read(1) << 5;       
            r[0] |= br.Read(7);        
            b[3] |= br.Read(1);            
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(1) << 4;       
            g[0] |= br.Read(7);        
            b[2] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 2;       
            g[2] |= br.Read(1) << 4;       
            b[0] |= br.Read(7);        
            b[3] |= br.Read(1) << 3;       
            b[3] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 4;       
            r[1] |= br.Read(6);        
            g[2] |= br.Read(4);        
            g[1] |= br.Read(6);        
            g[3] |= br.Read(4);        
            b[1] |= br.Read(6);        
            b[2] |= br.Read(4);        
            r[2] |= br.Read(6);        
            r[3] |= br.Read(6);        
            partition = br.Read(5);    
            mode = 1;
        } break;

        
        case 0x2: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(5);        
            r[0] |= br.Read(1) << 10;      
            g[2] |= br.Read(4);        
            g[1] |= br.Read(4);        
            g[0] |= br.Read(1) << 10;      
            b[3] |= br.Read(1);            
            g[3] |= br.Read(4);        
            b[1] |= br.Read(4);        
            b[0] |= br.Read(1) << 10;      
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(4);        
            r[2] |= br.Read(5);        
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(5);        
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 2;
        } break;

        
        case 0x6: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(4);        
            r[0] |= br.Read(1) << 10;      
            g[3] |= br.Read(1) << 4;       
            g[2] |= br.Read(4);        
            g[1] |= br.Read(5);        
            g[0] |= br.Read(1) << 10;      
            g[3] |= br.Read(4);        
            b[1] |= br.Read(4);        
            b[0] |= br.Read(1) << 10;      
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(4);        
            r[2] |= br.Read(4);        
            b[3] |= br.Read(1);            
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(4);        
            g[2] |= br.Read(1) << 4;       
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 3;
        } break;

        
        case 0xA: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(4);        
            r[0] |= br.Read(1) << 10;      
            b[2] |= br.Read(1) << 4;       
            g[2] |= br.Read(4);        
            g[1] |= br.Read(4);        
            g[0] |= br.Read(1) << 10;      
            b[3] |= br.Read(1);            
            g[3] |= br.Read(4);        
            b[1] |= br.Read(5);        
            b[0] |= br.Read(1) << 10;      
            b[2] |= br.Read(4);        
            r[2] |= br.Read(4);        
            b[3] |= br.Read(1) << 1;       
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(4);        
            b[3] |= br.Read(1) << 4;        
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 4;
        } break;

        
        case 0xE: {
            r[0] |= br.Read(9);        
            b[2] |= br.Read(1) << 4;       
            g[0] |= br.Read(9);        
            g[2] |= br.Read(1) << 4;       
            b[0] |= br.Read(9);        
            b[3] |= br.Read(1) << 4;       
            r[1] |= br.Read(5);        
            g[3] |= br.Read(1) << 4;       
            g[2] |= br.Read(4);        
            g[1] |= br.Read(5);        
            b[3] |= br.Read(1);            
            g[3] |= br.Read(4);        
            b[1] |= br.Read(5);        
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(4);        
            r[2] |= br.Read(5);        
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(5);        
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 5;
        } break;

        
        case 0x12: {
            r[0] |= br.Read(8);        
            g[3] |= br.Read(1) << 4;       
            b[2] |= br.Read(1) << 4;       
            g[0] |= br.Read(8);        
            b[3] |= br.Read(1) << 2;       
            g[2] |= br.Read(1) << 4;       
            b[0] |= br.Read(8);        
            b[3] |= br.Read(1) << 3;       
            b[3] |= br.Read(1) << 4;       
            r[1] |= br.Read(6);        
            g[2] |= br.Read(4);        
            g[1] |= br.Read(5);        
            b[3] |= br.Read(1);            
            g[3] |= br.Read(4);        
            b[1] |= br.Read(5);        
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(4);        
            r[2] |= br.Read(6);        
            r[3] |= br.Read(6);        
            partition = br.Read(5);    
            mode = 6;
        } break;

        
        case 0x16: {
            r[0] |= br.Read(8);        
            b[3] |= br.Read(1);            
            b[2] |= br.Read(1) << 4;       
            g[0] |= br.Read(8);        
            g[2] |= br.Read(1) << 5;       
            g[2] |= br.Read(1) << 4;       
            b[0] |= br.Read(8);        
            g[3] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 4;       
            r[1] |= br.Read(5);        
            g[3] |= br.Read(1) << 4;       
            g[2] |= br.Read(4);        
            g[1] |= br.Read(6);        
            g[3] |= br.Read(4);        
            b[1] |= br.Read(5);        
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(4);        
            r[2] |= br.Read(5);        
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(5);        
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 7;
        } break;

        
        case 0x1A: {
            r[0] |= br.Read(8);        
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(1) << 4;       
            g[0] |= br.Read(8);        
            b[2] |= br.Read(1) << 5;       
            g[2] |= br.Read(1) << 4;       
            b[0] |= br.Read(8);        
            b[3] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 4;       
            r[1] |= br.Read(5);        
            g[3] |= br.Read(1) << 4;       
            g[2] |= br.Read(4);        
            g[1] |= br.Read(5);        
            b[3] |= br.Read(1);            
            g[3] |= br.Read(4);        
            b[1] |= br.Read(6);        
            b[2] |= br.Read(4);        
            r[2] |= br.Read(5);        
            b[3] |= br.Read(1) << 2;       
            r[3] |= br.Read(5);        
            b[3] |= br.Read(1) << 3;       
            partition = br.Read(5);    
            mode = 8;
        } break;

        
        case 0x1E: {
            r[0] |= br.Read(6);        
            g[3] |= br.Read(1) << 4;       
            b[3] |= br.Read(1);            
            b[3] |= br.Read(1) << 1;       
            b[2] |= br.Read(1) << 4;       
            g[0] |= br.Read(6);        
            g[2] |= br.Read(1) << 5;       
            b[2] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 2;       
            g[2] |= br.Read(1) << 4;       
            b[0] |= br.Read(6);        
            g[3] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 3;       
            b[3] |= br.Read(1) << 5;       
            b[3] |= br.Read(1) << 4;       
            r[1] |= br.Read(6);        
            g[2] |= br.Read(4);        
            g[1] |= br.Read(6);        
            g[3] |= br.Read(4);        
            b[1] |= br.Read(6);        
            b[2] |= br.Read(4);        
            r[2] |= br.Read(6);        
            r[3] |= br.Read(6);        
            partition = br.Read(5);    
            mode = 9;
        } break;

        
        case 0x3: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(10);       
            g[1] |= br.Read(10);       
            b[1] |= br.Read(10);       
            mode = 10;
        } break;

        
        case 0x7: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(9);        
            r[0] |= br.Read(1) << 10;      
            g[1] |= br.Read(9);        
            g[0] |= br.Read(1) << 10;      
            b[1] |= br.Read(9);        
            b[0] |= br.Read(1) << 10;      
            mode = 11;
        } break;

        
        case 0xB: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(8);        
            r[0] |= br.ReadReverse(2) << 10;
            g[1] |= br.Read(8);        
            g[0] |= br.ReadReverse(2) << 10;
            b[1] |= br.Read(8);        
            b[0] |= br.ReadReverse(2) << 10;
            mode = 12;
        } break;

        
        case 0xF: {
            r[0] |= br.Read(10);       
            g[0] |= br.Read(10);       
            b[0] |= br.Read(10);       
            r[1] |= br.Read(4);        
            r[0] |= br.ReadReverse(6) << 10;
            g[1] |= br.Read(4);        
            g[0] |= br.ReadReverse(6) << 10;
            b[1] |= br.Read(4);        
            b[0] |= br.ReadReverse(6) << 10;
            mode = 13;
        } break;

        default: { mode = -1; } break;
    }
            if (mode < 0) return;

            int numPartitions = (mode >= 10) ? 0 : 1;
            int actualBits0Mode = Bc6hBits[0, mode];

            if (signed)
            {
                r[0] = ExtendSign(r[0], actualBits0Mode);
                g[0] = ExtendSign(g[0], actualBits0Mode);
                b[0] = ExtendSign(b[0], actualBits0Mode);
            }

            if ((mode != 9 && mode != 10) || signed)
            {
                for (int i = 1; i < (numPartitions + 1) * 2; i++)
                {
                    r[i] = ExtendSign(r[i], Bc6hBits[1, mode]);
                    g[i] = ExtendSign(g[i], Bc6hBits[2, mode]);
                    b[i] = ExtendSign(b[i], Bc6hBits[3, mode]);
                }
            }

            if (mode != 9 && mode != 10)
            {
                for (int i = 1; i < (numPartitions + 1) * 2; i++)
                {
                    r[i] = TransformInverse(r[i], r[0], actualBits0Mode, signed);
                    g[i] = TransformInverse(g[i], g[0], actualBits0Mode, signed);
                    b[i] = TransformInverse(b[i], b[0], actualBits0Mode, signed);
                }
            }

            int endpointCount = (numPartitions + 1) * 2;
            for (int i = 0; i < endpointCount; i++)
            {
                r[i] = Unquantize(r[i], actualBits0Mode, signed);
                g[i] = Unquantize(g[i], actualBits0Mode, signed);
                b[i] = Unquantize(b[i], actualBits0Mode, signed);
            }

            int[] weights = mode >= 10 ? Weight4 : Weight3;

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    int subset = mode >= 10 ? ((i | j) == 0 ? 128 : 0) : Bc6hPartitionRaw[partition, i, j];
                    int indexBits = mode >= 10 ? 4 : 3;
                    if ((subset & 0x80) != 0) indexBits--;

                    int index = br.Read(indexBits);
                    int ep = (subset & 1) * 2;
                    byte rr = ToneMap(HalfToFloat(FinishUnquantize(Interpolate(r[ep], r[ep + 1], weights, index), signed)));
                    byte gg = ToneMap(HalfToFloat(FinishUnquantize(Interpolate(g[ep], g[ep + 1], weights, index), signed)));
                    byte bb = ToneMap(HalfToFloat(FinishUnquantize(Interpolate(b[ep], b[ep + 1], weights, index), signed)));

                    int px = bx + j;
                    int py = by + i;
                    if (px < width && py < height)
                    {
                        int o = (py * width + px) * 4;
                        rgba[o] = bb;
                        rgba[o + 1] = gg;
                        rgba[o + 2] = rr;
                        rgba[o + 3] = 255;
                    }
                }
            }
        }

        public static byte[] DecodeBc7(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            int p = offset;
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 4)
                {
                    if (p + 16 > data.Length) return rgba;
                    DecodeBc7Block(data, p, rgba, width, height, bx, by);
                    p += 16;
                }
            }
            return rgba;
        }

        private static void DecodeBc7Block(byte[] data, int offset, byte[] rgba, int width, int height, int bx, int by)
        {
            BitReader br = new BitReader();
            br.Low = BitConverter.ToUInt64(data, offset);
            br.High = BitConverter.ToUInt64(data, offset + 8);

            int mode = 0;
            while (mode < 8 && br.Read(1) == 0) mode++;
            if (mode >= 8) return;

            int partition = 0;
            int numPartitions = 1;
            int rotation = 0;
            int indexSelection = 0;

            if (mode == 0 || mode == 1 || mode == 2 || mode == 3 || mode == 7)
            {
                numPartitions = (mode == 0 || mode == 2) ? 3 : 2;
                partition = br.Read(mode == 0 ? 4 : 6);
            }

            if (mode == 4 || mode == 5)
            {
                rotation = br.Read(2);
                if (mode == 4) indexSelection = br.Read(1);
            }

            int numEndpoints = numPartitions * 2;
            int colorBits = Bc7ColorBits[mode];
            int alphaBits = Bc7AlphaBits[mode];
            int[,] endpoints = new int[6, 4];

            for (int c = 0; c < 3; c++)
                for (int e = 0; e < numEndpoints; e++)
                    endpoints[e, c] = br.Read(colorBits);
            if (alphaBits > 0)
                for (int e = 0; e < numEndpoints; e++)
                    endpoints[e, 3] = br.Read(alphaBits);

            bool hasPBits = ((0xCB >> mode) & 1) != 0;
            if (hasPBits)
            {
                for (int e = 0; e < numEndpoints; e++)
                    for (int c = 0; c < 4; c++)
                        endpoints[e, c] <<= 1;

                if (mode == 1)
                {
                    int p0 = br.Read(1);
                    int p1 = br.Read(1);
                    for (int c = 0; c < 3; c++)
                    {
                        endpoints[0, c] |= p0;
                        endpoints[1, c] |= p0;
                        endpoints[2, c] |= p1;
                        endpoints[3, c] |= p1;
                    }
                }
                else
                {
                    for (int e = 0; e < numEndpoints; e++)
                    {
                        int p = br.Read(1);
                        for (int c = 0; c < 4; c++) endpoints[e, c] |= p;
                    }
                }
            }

            for (int e = 0; e < numEndpoints; e++)
            {
                int bits = colorBits + (hasPBits ? 1 : 0);
                for (int c = 0; c < 3; c++)
                    endpoints[e, c] = (endpoints[e, c] << (8 - bits)) | (endpoints[e, c] >> bits);
                bits = alphaBits + (hasPBits ? 1 : 0);
                endpoints[e, 3] = (endpoints[e, 3] << (8 - bits)) | (endpoints[e, 3] >> bits);
                if (alphaBits == 0) endpoints[e, 3] = 255;
            }

            int indexBits = (mode == 0 || mode == 1) ? 3 : (mode == 6 ? 4 : 2);
            int indexBits2 = mode == 4 ? 3 : (mode == 5 ? 2 : 0);
            int[] weights = indexBits == 2 ? Weight2 : (indexBits == 3 ? Weight3 : Weight4);
            int[] weights2 = indexBits2 == 2 ? Weight2 : Weight3;
            int[] indices = new int[16];

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    int subset = Bc7Subset(numPartitions, partition, i, j);
                    int bits = indexBits;
                    if ((subset & 0x80) != 0) bits--;
                    indices[i * 4 + j] = br.Read(bits);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    int subset = Bc7Subset(numPartitions, partition, i, j);
                    int index = indices[i * 4 + j];
                    int r, g, b, a;
                    if (indexBits2 == 0)
                    {
                        int e = (subset & 3) * 2;
                        r = Interpolate(endpoints[e, 0], endpoints[e + 1, 0], weights, index);
                        g = Interpolate(endpoints[e, 1], endpoints[e + 1, 1], weights, index);
                        b = Interpolate(endpoints[e, 2], endpoints[e + 1, 2], weights, index);
                        a = Interpolate(endpoints[e, 3], endpoints[e + 1, 3], weights, index);
                    }
                    else
                    {
                        int bits2 = (i | j) == 0 ? indexBits2 - 1 : indexBits2;
                        int index2 = br.Read(bits2);
                        int e = (subset & 3) * 2;
                        if (indexSelection == 0)
                        {
                            r = Interpolate(endpoints[e, 0], endpoints[e + 1, 0], weights, index);
                            g = Interpolate(endpoints[e, 1], endpoints[e + 1, 1], weights, index);
                            b = Interpolate(endpoints[e, 2], endpoints[e + 1, 2], weights, index);
                            a = Interpolate(endpoints[e, 3], endpoints[e + 1, 3], weights2, index2);
                        }
                        else
                        {
                            r = Interpolate(endpoints[e, 0], endpoints[e + 1, 0], weights2, index2);
                            g = Interpolate(endpoints[e, 1], endpoints[e + 1, 1], weights2, index2);
                            b = Interpolate(endpoints[e, 2], endpoints[e + 1, 2], weights2, index2);
                            a = Interpolate(endpoints[e, 3], endpoints[e + 1, 3], weights, index);
                        }
                    }

                    if (rotation == 1) { int t = a; a = r; r = t; }
                    else if (rotation == 2) { int t = a; a = g; g = t; }
                    else if (rotation == 3) { int t = a; a = b; b = t; }

                    int px = bx + j;
                    int py = by + i;
                    if (px < width && py < height)
                    {
                        int o = (py * width + px) * 4;
                        rgba[o] = (byte)b;
                        rgba[o + 1] = (byte)g;
                        rgba[o + 2] = (byte)r;
                        rgba[o + 3] = (byte)a;
                    }
                }
            }
        }

        private static int Bc7Subset(int numPartitions, int partition, int i, int j)
        {
            if (numPartitions == 1) return (i | j) == 0 ? 128 : 0;
            return Bc7PartitionRaw[numPartitions - 2, partition, i, j];
        }

        private static int ExtendSign(int value, int bits)
        {
            return (value << (32 - bits)) >> (32 - bits);
        }

        private static int TransformInverse(int value, int anchor, int bits, bool signed)
        {
            value = (value + anchor) & ((1 << bits) - 1);
            if (signed) value = ExtendSign(value, bits);
            return value;
        }

        private static int Unquantize(int value, int bits, bool signed)
        {
            int result;
            if (!signed)
            {
                if (bits >= 15) result = value;
                else if (value == 0) result = 0;
                else if (value == ((1 << bits) - 1)) result = 0xFFFF;
                else result = ((value << 16) + 0x8000) >> bits;
            }
            else
            {
                if (bits >= 16) result = value;
                else
                {
                    int sign = 0;
                    if (value < 0) { sign = 1; value = -value; }
                    if (value == 0) result = 0;
                    else if (value >= ((1 << (bits - 1)) - 1)) result = 0x7FFF;
                    else result = ((value << 15) + 0x4000) >> (bits - 1);
                    if (sign != 0) result = -result;
                }
            }
            return result;
        }

        private static int Interpolate(int a, int b, int[] weights, int index)
        {
            return (a * (64 - weights[index]) + b * weights[index] + 32) >> 6;
        }

        private static ushort FinishUnquantize(int value, bool signed)
        {
            if (!signed)
            {
                return (ushort)((value * 31) >> 6);
            }

            value = value < 0 ? -(((-value) * 31) >> 5) : (value * 31) >> 5;
            int sign = 0;
            if (value < 0) { sign = 0x8000; value = -value; }
            return (ushort)(sign | value);
        }

        private static float HalfToFloat(ushort half)
        {
            FloatUnion o = new FloatUnion();
            o.U = (uint)(half & 0x7FFF) << 13;
            uint exp = (0x7C00u << 13) & o.U;
            o.U += (uint)(127 - 15) << 23;
            if (exp == (0x7C00u << 13))
            {
                o.U += (uint)(128 - 16) << 23;
            }
            else if (exp == 0)
            {
                o.U += 1u << 23;
                o.F -= 6.103515625e-05f;
            }
            o.U |= (uint)(half & 0x8000) << 16;
            return o.F;
        }

        private static byte ToneMap(float value)
        {
            if (!(value > 0f)) return 0;
            float exposed = value * 4f;
            float mapped = exposed / (1f + exposed);
            if (mapped >= 1f) return 255;
            int index = (int)(mapped * 1023f + 0.5f);
            if (index < 0) index = 0;
            else if (index > 1023) index = 1023;
            return GammaLut[index];
        }

        private static byte[] BuildGammaLut()
        {
            byte[] lut = new byte[1024];
            for (int i = 0; i < lut.Length; i++)
            {
                lut[i] = (byte)Math.Round(255.0 * Math.Pow(i / 1023.0, 1.0 / 2.2));
            }
            return lut;
        }
    }
}