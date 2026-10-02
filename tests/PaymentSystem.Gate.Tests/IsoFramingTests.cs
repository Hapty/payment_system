using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

public class IsoFramingTests
{
    [Fact]
    public async Task WriteThenRead_ReturnsSameBytes()
    {
        var stream = new MemoryStream();
        await IsoFraming.WriteAsync(stream, [1, 2, 3]);

        Assert.Equal(new byte[] { 0, 3, 1, 2, 3 }, stream.ToArray());   // 2-byte big-endian length header

        stream.Position = 0;
        Assert.Equal(new byte[] { 1, 2, 3 }, await IsoFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_TwoFramesBackToBack_ReadsThemSeparately()
    {
        var stream = new MemoryStream([0, 1, 0xAA, 0, 2, 0xBB, 0xCC]);

        Assert.Equal(new byte[] { 0xAA }, await IsoFraming.ReadAsync(stream));
        Assert.Equal(new byte[] { 0xBB, 0xCC }, await IsoFraming.ReadAsync(stream));
        Assert.Null(await IsoFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_EmptyStream_ReturnsNull() =>
        Assert.Null(await IsoFraming.ReadAsync(new MemoryStream()));

    [Fact]
    public async Task Read_MessageArrivingInPieces_IsReassembled()
    {
        var stream = new OneByteAtATimeStream([0, 4, 1, 2, 3, 4]);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await IsoFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_HalfHeader_Throws() =>
        await Assert.ThrowsAsync<EndOfStreamException>(() => IsoFraming.ReadAsync(new MemoryStream([0])));

    [Fact]
    public async Task Read_BodyShorterThanHeaderSays_Throws() =>
        await Assert.ThrowsAsync<EndOfStreamException>(() => IsoFraming.ReadAsync(new MemoryStream([0, 5, 1, 2])));

    [Theory]
    [InlineData(0x00, 0x00)]    // 0
    [InlineData(0x20, 0x01)]    // 8193 > MaxMessageLength
    public async Task Read_InvalidLength_Throws(byte high, byte low) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => IsoFraming.ReadAsync(new MemoryStream([high, low])));

    [Fact]
    public async Task Write_EmptyMessage_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() => IsoFraming.WriteAsync(new MemoryStream(), []));
}
