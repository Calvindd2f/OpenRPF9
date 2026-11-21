using OpenRPF9.Core;
using Xunit;

namespace OpenRPF9.Tests;

public class Rpf9ProbeTests
{
    [Fact]
    public void ReadsRpf9Magic()
    {
        var data = new byte[32];
        data[0] = (byte)'R';
        data[1] = (byte)'P';
        data[2] = (byte)'F';
        data[3] = (byte)'9';
        var probe = Rpf9HeaderProbe.Read(data);
        Assert.Equal(0x52504639u, probe.Magic);
    }

    [Fact]
    public void DetectsZlibSignatures()
    {
        byte[] data = [0x00, 0x78, 0x9C, 0x01, 0x00, 0x78, 0xDA, 0x00];
        var hits = Rpf9Scanner.FindZlibStreams(data);
        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0]);
        Assert.Equal(5, hits[1]);
    }
}

public class CryptoCompressionTests
{
    [Fact]
    public void AesRoundTripBlock()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var plain = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        var enc = aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
        var dec = RpfCrypto.DecryptAes(enc, key);
        Assert.Equal(plain, dec);
    }
}

public class ModdingTests
{
    [Fact]
    public void JoaatIsStable()
    {
        var hash = OpenRPF9.Modding.HashPathCache.Joaat("common.rpf");
        Assert.Equal(OpenRPF9.Modding.HashPathCache.Joaat("COMMON.RPF"), hash);
    }
}
