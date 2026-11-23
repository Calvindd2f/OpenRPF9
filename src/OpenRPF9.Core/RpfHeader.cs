using System;
using System.Collections.Generic;
using System.IO;
namespace OpenRPF9.Core
{
public sealed class RpfHeader
{
    public RpfVersion Version { get; set; }
    public uint EntryCount { get; set; }
    public uint NamesLength { get; set; }
    public RpfEncryption Encryption { get; set; }
    public uint NameShift { get; set; }
    public bool HeaderFlag { get; set; }
    public uint RawFlags { get; set; }
}

public sealed class RpfArchiveInfo
{
    public string Path { get; set; }
    public long FileSize { get; set; }
    public RpfHeader Header { get; set; }
    public RpfDirectoryEntry Root { get; set; }
    public System.Collections.Generic.IList<RpfEntry> Entries { get; set; }
}
}
