using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OpenRPF9.Core
{
    public static class RpfCompression
    {
        public static byte[] Decompress(byte[] source, int expectedSize)
        {
            if (expectedSize <= 0) return source;
            byte[] output = new byte[expectedSize];
            int written = Decompress(source, output);
            if (written == expectedSize) return output;
            byte[] trimmed = new byte[Math.Max(0, written)];
            if (written > 0) Buffer.BlockCopy(output, 0, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        public static int Decompress(byte[] source, byte[] destination)
        {
            int total = 0;
            try
            {
                using (MemoryStream input = new MemoryStream(source, 0, source.Length, false, true))
                using (DeflateStream zlib = new DeflateStream(input, CompressionMode.Decompress, false))
                {
                    while (total < destination.Length)
                    {
                        int read = zlib.Read(destination, total, destination.Length - total);
                        if (read == 0) break;
                        total += read;
                    }
                }
            }
            catch { total = 0; }
            if (total == 0)
            {
                try
                {
                    using (MemoryStream input = new MemoryStream(source, 0, source.Length, false, true))
                    using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress, false))
                    {
                        while (total < destination.Length)
                        {
                            int read = deflate.Read(destination, total, destination.Length - total);
                            if (read == 0) break;
                            total += read;
                        }
                    }
                }
                catch { }
            }
            return total;
        }
    }

    public static class RpfCrypto
    {
        public static byte[] DecryptAes(byte[] data, byte[] key)
        {
            if (data == null || data.Length == 0) return data;
            if (key == null || key.Length != 32) throw new RpfCryptoException("AES key must be 32 bytes.");
            byte[] result = new byte[data.Length];
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
            int aligned = result.Length - (result.Length % 16);
            if (aligned > 0)
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
                    using (ICryptoTransform dec = aes.CreateDecryptor())
                    {
                        dec.TransformBlock(result, 0, aligned, result, 0);
                    }
                }
            }
            return result;
        }

        public static byte[] DecryptNg(byte[] data, string name, uint length, byte[] key)
        {
            if (data == null || data.Length == 0) return data;
            if (key == null || key.Length == 0) throw new RpfCryptoException("NG key required.");
            byte[] result = new byte[data.Length];
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
            return result;
        }
    }
}
