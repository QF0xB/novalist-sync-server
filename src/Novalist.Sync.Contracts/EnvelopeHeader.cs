using System.Buffers.Binary;
using Novalist.Sync.Contracts.Exceptions;

namespace Novalist.Sync.Contracts;

public sealed record EnvelopeHeader(
    byte ProtoVersion,
    MsgType MsgType,
    Guid ProjectId,
    Guid ItemId,
    uint Epoch,
    Guid SenderDeviceId,
    ulong SenderSeq)
{
    public const int Size = 62;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination buffer is too small. Expected at least {Size} bytes, got {destination.Length}.", nameof(destination));

        destination[0] = ProtoVersion;
        destination[1] = (byte)MsgType;
        ProjectId.TryWriteBytes(destination[2..18], bigEndian: true, out _);
        ItemId.TryWriteBytes(destination[18..34], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(destination[34..38], Epoch);
        SenderDeviceId.TryWriteBytes(destination[38..54], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(destination[54..62], SenderSeq);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }

    public static EnvelopeHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            throw new FormatException("Header is empty.");
        }

        if (source[0] != Protocol.Version)
        {
            throw new UnsupportedProtocolVersionException(source[0]);
        }

        if (source.Length != Size)
        {
            throw new FormatException($"Header must be {Size} bytes, got {source.Length}.");
        }

        var msgType = (MsgType)source[1];
        if (!Enum.IsDefined(msgType))
        {
            throw new FormatException($"Unknown msgType {source[1]}");
        }

        return new EnvelopeHeader(
            ProtoVersion: source[0],
            MsgType: msgType,
            ProjectId: new Guid(source[2..18], bigEndian: true),
            ItemId: new Guid(source[18..34], bigEndian: true),
            Epoch: BinaryPrimitives.ReadUInt32BigEndian(source[34..38]),
            SenderDeviceId: new Guid(source[38..54], bigEndian: true),
            SenderSeq: BinaryPrimitives.ReadUInt64BigEndian(source[54..62]));
    }
}