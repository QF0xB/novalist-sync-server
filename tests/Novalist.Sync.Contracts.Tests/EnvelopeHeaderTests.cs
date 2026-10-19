using Novalist.Sync.Contracts.Exceptions;

namespace Novalist.Sync.Contracts.Tests;

public class EnvelopeHeaderTests
{
    private static EnvelopeHeader SampleHeader() => new(
        ProtoVersion: 1,
        MsgType: MsgType.Snapshot,
        ProjectId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        ItemId: Guid.Parse("10111213-1415-1617-1819-1a1b1c1d1e1f"),
        Epoch: 0x01020304,
        SenderDeviceId: Guid.Parse("20212223-2425-2627-2829-2a2b2c2d2e2f"),
        SenderSeq: 0x0102030405060708);

    [Fact]
    public void ToBytes_WritesSpecLayout()
    {
        byte[] expected =
        [
            0x01,                                           // protoVersion
            0x02,                                           // msgType = Snapshot
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, // projectId
            0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, // itemId
            0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
            0x01, 0x02, 0x03, 0x04,                         // epoch
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, // senderDeviceId
            0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // senderSeq
        ];

        Assert.Equal(EnvelopeHeader.Size, expected.Length);
        Assert.Equal(expected, SampleHeader().ToBytes());
    }

    [Fact]
    public void WriteTo_RejectsTooSmallBuffer()
    {
        var header = SampleHeader();

        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[EnvelopeHeader.Size - 1]));
    }

    [Fact]
    public void ToBytes_HasCorrectSize()
    {
        Assert.Equal(EnvelopeHeader.Size, SampleHeader().ToBytes().Length);
    }

    [Fact]
    public void Parse_RoundTripsToBytes()
    {
        var header = SampleHeader();

        Assert.Equal(header, EnvelopeHeader.Parse(header.ToBytes()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(2)]
    public void Parse_RejectsUnsupportedProtoVersion(byte version)
    {
        var bytes = SampleHeader().ToBytes();
        bytes[0] = version;

        var ex = Assert.Throws<UnsupportedProtocolVersionException>(() => EnvelopeHeader.Parse(bytes));
        Assert.Equal(version, ex.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(4)]
    public void Parse_RejectsUnknownMsgType(byte msgType)
    {
        var bytes = SampleHeader().ToBytes();
        bytes[1] = msgType;

        Assert.Throws<FormatException>(() => EnvelopeHeader.Parse(bytes));
    }

    [Theory]
    [InlineData(EnvelopeHeader.Size - 1)]
    [InlineData(EnvelopeHeader.Size + 1)]
    public void Parse_RejectsWrongLength(int length)
    {
        var bytes = new byte[length];
        SampleHeader().ToBytes().AsSpan(0, Math.Min(length, EnvelopeHeader.Size)).CopyTo(bytes);

        Assert.Throws<FormatException>(() => EnvelopeHeader.Parse(bytes));
    }
}