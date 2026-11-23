using System;
using System.IO;

namespace OpenRPF9.Modding
{
    public sealed class RpfCacheBypass
    {
        public string PreparePatchedCache(string cachePath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(cachePath)) throw new ArgumentException("cachePath");
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("outputPath");
            if (!File.Exists(cachePath)) throw new FileNotFoundException("rpf.cache not found.", cachePath);

            byte[] bytes = File.ReadAllBytes(cachePath);
            for (int i = 0; i + 3 < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'R' && bytes[i + 1] == (byte)'P' && bytes[i + 2] == (byte)'F')
                {
                    bytes[i + 3] = (byte)'0';
                }
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, bytes);
            return Path.GetFullPath(outputPath);
        }
    }
}
