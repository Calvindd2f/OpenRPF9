using System;
using System.Collections.Generic;
using System.IO;
namespace OpenRPF9.Core
{
public sealed class RpfFormatException : Exception
{
    public RpfFormatException(string message) : base(message) { }
    public RpfFormatException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class RpfCryptoException : Exception
{
    public RpfCryptoException(string message) : base(message) { }
    public RpfCryptoException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class RpfResourceException : Exception
{
    public RpfResourceException(string message) : base(message) { }
    public RpfResourceException(string message, Exception innerException) : base(message, innerException) { }
}
}
