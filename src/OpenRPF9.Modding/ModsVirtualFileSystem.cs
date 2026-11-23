using System;
using System.IO;

namespace OpenRPF9.Modding
{
    public sealed class ModsVirtualFileSystem
    {
        private readonly string _modsRoot;
        private readonly HashPathCache _cache;

        public ModsVirtualFileSystem(string modsRoot, HashPathCache cache)
        {
            _modsRoot = Path.GetFullPath(modsRoot);
            _cache = cache != null ? cache : new HashPathCache();
        }

        public ModsVirtualFileSystem(string modsRoot) : this(modsRoot, null) { }

        public string ModsRoot { get { return _modsRoot; } }
        public HashPathCache Cache { get { return _cache; } }

        public bool TryResolve(string virtualPath, out string physicalPath)
        {
            string normalized = HashPathCache.Normalize(virtualPath);
            string cached;
            if (_cache.TryGetPhysical(normalized, out cached) && !string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
            {
                physicalPath = cached;
                return true;
            }

            string candidate = Path.Combine(_modsRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
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
            if (!Directory.Exists(_modsRoot)) return;
            foreach (string file in Directory.EnumerateFiles(_modsRoot, "*", SearchOption.AllDirectories))
            {
                string rel = file.StartsWith(_modsRoot, StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(_modsRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : Path.GetFileName(file);
                _cache.Set(rel, file);
            }
        }
    }
}
