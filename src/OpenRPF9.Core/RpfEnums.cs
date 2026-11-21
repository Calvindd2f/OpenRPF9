namespace OpenRPF9.Core;

public enum RpfVersion : uint
{
    Rpf7 = 0x52504637,
    Rpf8 = 0x52504638,
    Rpf9 = 0x52504639
}

public enum RpfEncryption : uint
{
    None = 0,
    Open = 0x4E45504F,
    Aes = 0x0FFFFFF9,
    Ng = 0x0FEFFFFF
}

public enum RpfEntryKind
{
    Directory,
    BinaryFile,
    ResourceFile
}
