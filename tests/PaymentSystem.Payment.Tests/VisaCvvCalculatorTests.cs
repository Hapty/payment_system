using PaymentSystem.Payment.Cards;

namespace PaymentSystem.Payment.Tests;

// Testlerin ortak CVV anahtarı (gerçek ortamda CVV_KEY).
public static class TestCvv
{
    public static VisaCvvCalculator Calculator { get; } = new("00112233445566778899AABBCCDDEEFF");
}

public class VisaCvvCalculatorTests
{
    // Visa CVV algoritmasının yaygın kullanılan örnek vektörü.
    private const string TestKey = "0123456789ABCDEFFEDCBA9876543210";

    [Fact]
    public void Calculate_MatchesPublishedVisaTestVector() =>
        Assert.Equal("561", new VisaCvvCalculator(TestKey).Calculate("4123456789012345", yymm: "8701", serviceCode: "101"));

    [Fact]
    public void Cvv2_IsDeterministicAndDependsOnCardAndExpiry()
    {
        var calculator = new VisaCvvCalculator(TestKey);

        var cvv = calculator.CalculateCvv2("4111111111111111", "1228");

        Assert.Matches("^[0-9]{3}$", cvv);
        Assert.Equal(cvv, calculator.CalculateCvv2("4111111111111111", "1228"));
        // Card.ExpiryDate MMYY'dir; algoritmaya YYMM (2812) ve CVV2 servis kodu 000 ile girer.
        Assert.Equal(calculator.Calculate("4111111111111111", "2812", "000"), cvv);
        Assert.NotEqual(cvv, calculator.CalculateCvv2("4111111111111111", "1229"));
    }

    [Fact]
    public void Cvv2_DependsOnKey() =>
        Assert.NotEqual(
            new VisaCvvCalculator(TestKey).CalculateCvv2("4111111111111111", "1228"),
            new VisaCvvCalculator("FEDCBA98765432100123456789ABCDEF").CalculateCvv2("4111111111111111", "1228"));

    [Theory]
    [InlineData(null)]
    [InlineData("128")]
    public void Cvv2_WithoutValidExpiry_IsNull(string? expiry) =>
        Assert.Null(new VisaCvvCalculator(TestKey).CalculateCvv2("4111111111111111", expiry));

    [Fact]
    public void Cvv2_NonNumericCardNumber_IsNull() =>
        Assert.Null(new VisaCvvCalculator(TestKey).CalculateCvv2("4111-1111", "1228"));

    [Theory]
    [InlineData("0123")]                                    // kısa
    [InlineData("0123456789ABCDEFFEDCBA987654321G")]        // hex değil
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]        // KeyA == KeyB: tek anahtarlı DES'e düşer
    public void InvalidKey_IsRejected(string key) =>
        Assert.Throws<InvalidOperationException>(() => new VisaCvvCalculator(key));

    [Fact]
    public void KeyCheckValue_IsSixHexAndDoesNotRevealKey()
    {
        var kcv = new VisaCvvCalculator(TestKey).KeyCheckValue;

        Assert.Matches("^[0-9A-F]{6}$", kcv);
        Assert.DoesNotContain(kcv, TestKey);
    }
}
