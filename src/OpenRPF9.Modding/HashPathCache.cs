using System.Collections.Concurrent;

namespace OpenRPF9.Modding;

public sealed class HashPathCache
{
    readonly ConcurrentDictionary<string, string> _pathToPhysical = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<ulong, string> _hashToPath = new();

    public void Set(string virtualPath, string physicalPath)
    {
        var normalized = Normalize(virtualPath);
        _pathToPhysical[normalized] = physicalPath;
        _hashToPath[Joaat(normalized)] = normalized;
    }

    public bool TryGetPhysical(string virtualPath, out string? physicalPath)
    {
        return _pathToPhysical.TryGetValue(Normalize(virtualPath), out physicalPath);
    }

    public bool TryGetByHash(ulong hash, out string? virtualPath)
    {
        return _hashToPath.TryGetValue(hash, out virtualPath);
    }

    public static string Normalize(string path)
    {
        return path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
    }

    public static ulong Joaat(string value)
    {
        uint hash = 0;
        for (var i = 0; i < value.Length; i++)
        {
            hash += char.ToLowerInvariant(value[i]);
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}
