namespace OpenRPF9.Modding;

public sealed class ModsVirtualFileSystem
{
    readonly string _modsRoot;
    readonly HashPathCache _cache;

    public ModsVirtualFileSystem(string modsRoot, HashPathCache? cache = null)
    {
        _modsRoot = Path.GetFullPath(modsRoot);
        _cache = cache ?? new HashPathCache();
    }

    public string ModsRoot => _modsRoot;
    public HashPathCache Cache => _cache;

    public bool TryResolve(string virtualPath, out string physicalPath)
    {
        var normalized = HashPathCache.Normalize(virtualPath);
        if (_cache.TryGetPhysical(normalized, out var cached) && !string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
        {
            physicalPath = cached;
            return true;
        }

        var candidate = Path.Combine(_modsRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidate))
        {
            _cache.Set(normalized, candidate);
            physicalPath = candidate;
            return true;
        }

        physicalPath = string.Empty;
        return false;
    }

    public void IndexMods()
    {
        if (!Directory.Exists(_modsRoot))
            return;
        foreach (var file in Directory.EnumerateFiles(_modsRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_modsRoot, file);
            _cache.Set(rel, file);
        }
    }
}
