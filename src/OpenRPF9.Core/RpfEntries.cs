using System;
using System.Collections.Generic;
using System.IO;
namespace OpenRPF9.Core
{
public abstract class RpfEntry
{
    public string Name { get; set; }
    public uint NameOffset { get; set; }
    public RpfEntryKind Kind { get; set; }
    public RpfDirectoryEntry Parent { get; internal set; }
    public string FullPath { get { return Parent == null || Parent.Parent == null ? Name : string.Concat(Parent.FullPath, "/", Name); } }
}

public sealed class RpfDirectoryEntry : RpfEntry
{
    public uint EntriesIndex { get; set; }
    public uint EntriesCount { get; set; }
    public List<RpfDirectoryEntry> Directories { get { return _directories; } } private readonly List<RpfDirectoryEntry> _directories = new System.Collections.Generic.List<RpfDirectoryEntry>();
    public List<RpfFileEntry> Files { get { return _files; } } private readonly List<RpfFileEntry> _files = new System.Collections.Generic.List<RpfFileEntry>();
}

public abstract class RpfFileEntry : RpfEntry
{
    public uint FileOffset { get; set; }
    public uint FileSize { get; set; }
    public uint FileUncompressedSize { get; set; }
    public uint EncryptionType { get; set; }
}

public sealed class RpfBinaryFileEntry : RpfFileEntry
{
}

public sealed class RpfResourceFileEntry : RpfFileEntry
{
    public uint SystemFlags { get; set; }
    public uint GraphicsFlags { get; set; }
    public ushort Version { get; set; }
}
}
