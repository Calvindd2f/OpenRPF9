namespace OpenRPF9.Modding;

public sealed class RpfCacheBypass
{
    public string PreparePatchedCache(string cachePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(cachePath))
            throw new FileNotFoundException("rpf.cache not found.", cachePath);

        var bytes = File.ReadAllBytes(cachePath);
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'R' && bytes[i + 1] == (byte)'P' && bytes[i + 2] == (byte)'F')
            {
                bytes[i + 3] = (byte)'0';
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.WriteAllBytes(outputPath, bytes);
        return Path.GetFullPath(outputPath);
    }
}
