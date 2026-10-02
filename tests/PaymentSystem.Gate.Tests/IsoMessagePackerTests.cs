using System.Text;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

public class IsoMessagePackerTests
{
    private static readonly IsoMessagePacker Packer = IsoMessagePacker.Default;

    [Fact]
    public void PackThenUnpack_ReturnsSameMessage()
    {
        var original = TestMessages.Sale();

        var parsed = Packer.Unpack(Packer.Pack(original));

        Assert.Equal("0200", parsed.Mti);
        Assert.Equal(original.Fields, parsed.Fields);
    }

    [Fact]
    public void Pack_WritesMtiBitmapAndLlvarPrefix()
    {
        var bytes = Packer.Pack(new IsoMessage("0200").Set(2, "4111111111111111").Set(3, "000000"));

        Assert.Equal("0200", Encoding.ASCII.GetString(bytes, 0, 4));
        // Fields 2 and 3 -> bits 2 and 3 of the first bitmap byte: 0110 0000
        Assert.Equal(new byte[] { 0x60, 0, 0, 0, 0, 0, 0, 0 }, bytes[4..12]);
        // LLVAR: "16" length prefix, then the PAN, then the fixed 6-digit processing code
        Assert.Equal("164111111111111111000000", Encoding.ASCII.GetString(bytes, 12, bytes.Length - 12));
    }

    [Fact]
    public void Pack_FieldAbove64_AddsSecondaryBitmap()
    {
        var bytes = Packer.Pack(TestMessages.Echo());

        Assert.Equal(0x80, bytes[4] & 0x80);   // bit 1 = secondary bitmap present
        Assert.Equal(4 + 16 + 10 + 6 + 3, bytes.Length);
        Assert.Equal("301", Packer.Unpack(bytes)[70]);
    }

    [Theory]
    [InlineData(4, "15000", "exactly 12")]          // fixed field with wrong length
    [InlineData(4, "00000001500A", "Numeric")]      // letter in a numeric field
    [InlineData(2, "41111111111111111111", "between 1 and 19")]
    public void Pack_InvalidField_Throws(int field, string value, string reasonPart)
    {
        var ex = Assert.Throws<IsoFormatException>(() => Packer.Pack(new IsoMessage("0200").Set(field, value)));

        Assert.Equal(field, ex.FieldNumber);
        Assert.Contains(reasonPart, ex.Reason);
    }

    [Fact]
    public void Pack_UnsupportedField_Throws()
    {
        var ex = Assert.Throws<IsoFormatException>(() => Packer.Pack(new IsoMessage("0200").Set(55, "AB")));

        Assert.Equal(55, ex.FieldNumber);
    }

    [Fact]
    public void Pack_InvalidMti_Throws() =>
        Assert.Throws<IsoFormatException>(() => Packer.Pack(new IsoMessage("02X0")));

    [Fact]
    public void Unpack_TooShortForMti_ThrowsWithoutMti()
    {
        var ex = Assert.Throws<IsoFormatException>(() => Packer.Unpack("02"u8.ToArray()));

        Assert.Null(ex.Mti);
    }

    [Fact]
    public void Unpack_NonNumericMti_Throws() =>
        Assert.Throws<IsoFormatException>(() => Packer.Unpack("ABCD12345678"u8.ToArray()));

    [Fact]
    public void Unpack_TruncatedPrimaryBitmap_ThrowsWithMti()
    {
        var ex = Assert.Throws<IsoFormatException>(() => Packer.Unpack("0200\0\0\0"u8.ToArray()));

        Assert.Equal(1, ex.FieldNumber);
        Assert.Equal("0200", ex.Mti);
    }

    [Fact]
    public void Unpack_TruncatedSecondaryBitmap_Throws()
    {
        byte[] data = [.. "0800"u8, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(1, Assert.Throws<IsoFormatException>(() => Packer.Unpack(data)).FieldNumber);
    }

    [Fact]
    public void Unpack_TruncatedField_Throws()
    {
        var bytes = Packer.Pack(TestMessages.Sale());

        var ex = Assert.Throws<IsoFormatException>(() => Packer.Unpack(bytes[..^2]));

        Assert.Equal(49, ex.FieldNumber);
    }

    [Fact]
    public void Unpack_NonNumericLengthPrefix_Throws()
    {
        byte[] data = [.. "0200"u8, 0x40, 0, 0, 0, 0, 0, 0, 0, .. "1X4111"u8];

        Assert.Equal(2, Assert.Throws<IsoFormatException>(() => Packer.Unpack(data)).FieldNumber);
    }

    [Fact]
    public void Unpack_UnsupportedFieldInBitmap_Throws()
    {
        // Bit 5 (field 5) is not in Iso87Fields, so its length is unknown.
        byte[] data = [.. "0200"u8, 0x08, 0, 0, 0, 0, 0, 0, 0, .. "000000000000"u8];

        Assert.Equal(5, Assert.Throws<IsoFormatException>(() => Packer.Unpack(data)).FieldNumber);
    }

    [Fact]
    public void Unpack_InvalidFieldContent_Throws()
    {
        // Field 3 present with a letter in it.
        byte[] data = [.. "0200"u8, 0x20, 0, 0, 0, 0, 0, 0, 0, .. "00A000"u8];

        Assert.Equal(3, Assert.Throws<IsoFormatException>(() => Packer.Unpack(data)).FieldNumber);
    }

    [Fact]
    public void Unpack_TrailingBytes_Throws()
    {
        byte[] data = [.. Packer.Pack(TestMessages.Sale()), (byte)'X'];

        Assert.Equal(0, Assert.Throws<IsoFormatException>(() => Packer.Unpack(data)).FieldNumber);
    }

    [Theory]
    [InlineData("0200", "0210")]
    [InlineData("0800", "0810")]
    [InlineData("0400", "0410")]
    public void ToResponseMti_IncrementsFunctionDigit(string request, string expected) =>
        Assert.Equal(expected, new IsoMessage(request).ToResponseMti());

    [Fact]
    public void ToResponseMti_OnResponse_Throws()
    {
        Assert.False(new IsoMessage("0210").IsRequest);
        Assert.Throws<InvalidOperationException>(() => new IsoMessage("0210").ToResponseMti());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(129)]
    public void Set_FieldOutOfRange_Throws(int field) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new IsoMessage("0200").Set(field, "x"));

    [Theory]
    [InlineData("4111111111111111", "411111******1111")]
    [InlineData("1234", "****")]
    [InlineData(null, "")]
    public void PanMask_HidesMiddleDigits(string? pan, string expected) =>
        Assert.Equal(expected, PanMask.Mask(pan));
}
