using System.IO.Compression;
using System.Security.Cryptography;

namespace OpenRPF9.Core;

public static class RpfCompression
{
    public static byte[] Decompress(ReadOnlySpan<byte> source, int expectedSize)
    {
        if (expectedSize <= 0)
            return source.ToArray();
        var output = new byte[expectedSize];
        var written = Decompress(source, output);
        if (written == expectedSize)
            return output;
        if (written < expectedSize)
        {
            var trimmed = new byte[written];
            Buffer.BlockCopy(output, 0, trimmed, 0, written);
            return trimmed;
        }
        return output;
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        try
        {
            using var input = new MemoryStream(source.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
            var total = 0;
            while (total < destination.Length)
            {
                var read = zlib.Read(destination.Slice(total));
                if (read == 0)
                    break;
                total += read;
            }
            if (total == 0)
            {
                using var input2 = new MemoryStream(source.ToArray(), writable: false);
                using var deflate = new DeflateStream(input2, CompressionMode.Decompress, leaveOpen: false);
                while (total < destination.Length)
                {
                    var read = deflate.Read(destination.Slice(total));
                    if (read == 0)
                        break;
                    total += read;
                }
            }
            return total;
        }
        catch (InvalidDataException)
        {
            using var input = new MemoryStream(source.ToArray(), writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
            var total = 0;
            while (total < destination.Length)
            {
                var read = deflate.Read(destination.Slice(total));
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }
    }

    public static byte[] TryDecompress(ReadOnlySpan<byte> source)
    {
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var output = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            input.Position = 0;
            output.SetLength(0);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }
}

public static class RpfCrypto
{
    public static byte[] DecryptAes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new RpfCryptoException("AES-256 key must be 32 bytes.");
        var length = data.Length - (data.Length % 16);
        var output = data.ToArray();
        if (length == 0)
            return output;
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var decryptor = aes.CreateDecryptor();
        decryptor.TransformBlock(output, 0, length, output, 0);
        return output;
    }

    public static byte[] DecryptAes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, int rounds)
    {
        var result = data.ToArray();
        for (var i = 0; i < rounds; i++)
            result = DecryptAes(result, key);
        return result;
    }

    public static byte[] DecryptNg(ReadOnlySpan<byte> data, string name, uint length, ReadOnlySpan<byte> key)
    {
        if (key.Length == 0)
            throw new RpfCryptoException("NG key material is required.");
        var output = data.ToArray();
        var blockCount = output.Length / 16;
        if (blockCount == 0)
            return output;
        var nameHash = NgHash(name, length);
        var roundKeys = BuildNgRoundKeys(key, nameHash);
        for (var block = 0; block < blockCount; block++)
        {
            var offset = block * 16;
            for (var round = roundKeys.Length - 1; round >= 0; round--)
                NgTransformBlock(output, offset, roundKeys[round], block, round);
        }
        return output;
    }

    static uint NgHash(string name, uint length)
    {
        var hash = length;
        for (var i = 0; i < name.Length; i++)
        {
            hash += (uint)char.ToLowerInvariant(name[i]);
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    static byte[][] BuildNgRoundKeys(ReadOnlySpan<byte> key, uint seed)
    {
        var count = Math.Max(1, key.Length / 16);
        var keys = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            keys[i] = new byte[16];
            var source = key.Slice((i * 16) % key.Length, Math.Min(16, key.Length - ((i * 16) % key.Length)));
            source.CopyTo(keys[i]);
            var state = seed ^ (uint)(i * 0x9E3779B9u);
            for (var j = 0; j < 16; j++)
            {
                state = state * 1664525u + 1013904223u;
                keys[i][j] ^= (byte)(state >> 24);
            }
        }
        return keys;
    }

    static void NgTransformBlock(byte[] data, int offset, byte[] roundKey, int block, int round)
    {
        for (var i = 0; i < 16; i++)
        {
            var k = roundKey[i];
            var r = (byte)((round * 31) + (block * 17) + (i * 13));
            data[offset + i] = (byte)(data[offset + i] ^ k ^ r);
        }
        for (var i = 0; i < 15; i++)
        {
            var a = data[offset + i];
            var b = data[offset + i + 1];
            data[offset + i] = b;
            data[offset + i + 1] = a;
        }
    }
}
