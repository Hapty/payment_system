using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

public class IsoLogTests
{
    [Theory]
    [InlineData(2, "F2 (Kart No)")]
    [InlineData(11, "F11 (STAN)")]
    [InlineData(48, "F48 (Alıcı Kart No)")]
    [InlineData(55, "F55")]     // Iso87Fields'ta tanımlı değil
    public void Label_IsFieldNumberWithItsName(int field, string expected) =>
        Assert.Equal(expected, IsoLog.Label(field));

    [Fact]
    public void Field_MasksCardNumbersAndTrimsPadding()
    {
        Assert.Equal("F2 (Kart No)=411111********11", IsoLog.Field(2, "4111111111111111"));
        Assert.Equal("F48 (Alıcı Kart No)=540668********02", IsoLog.Field(48, "5406680000000002"));
        Assert.Equal("F43 (İşyeri Adı)=PAYMENT SYSTEM", IsoLog.Field(43, "PAYMENT SYSTEM".PadRight(40)));
        Assert.Equal("F11 (STAN)=-", IsoLog.Field(11, null));
    }

    [Fact]
    public void Fields_AreInFieldOrderWithoutMissingOnes() =>
        Assert.Equal("F2 (Kart No)=411111********11 F11 (STAN)=000001",
            IsoLog.Fields([new(11, "000001"), new(48, null), new(2, "4111111111111111")]));

    [Fact]
    public void Keys_LabelsFieldNumbersAndKeepsOtherNames() =>
        Assert.Equal("F11 (STAN), mti, cvv", IsoLog.Keys(["F11", "mti", "cvv"]));
}
