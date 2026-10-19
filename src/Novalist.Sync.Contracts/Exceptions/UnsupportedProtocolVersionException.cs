namespace Novalist.Sync.Contracts.Exceptions;

public sealed class UnsupportedProtocolVersionException(byte version)
: FormatException($"Unsupported protocol version {version}; this build supports version {Protocol.Version}.")
{
    public byte Version { get; } = version;
}