using System.Security.Cryptography;
using System.Text;
using PaymentSystem.Payment.Cards;

namespace PaymentSystem.Payment.Tests;

// Testlerin ortak hash anahtarı (gerçek ortamda CARD_SECRET_KEY).
public static class TestSecrets
{
    public static HmacCardSecretHasher Hasher { get; } = new("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");
}

public class HmacCardSecretHasherTests
{
    private const string Pan = "4111111111111111";

    [Fact]
    public void Hash_IsHmacSha256OfCardNumberKindAndValue()
    {
        var key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");

        Assert.Equal(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{Pan}:cvv:123")), TestSecrets.Hasher.HashCvv(Pan, "123"));
        Assert.Equal(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{Pan}:pin:1234")), TestSecrets.Hasher.HashPin(Pan, "1234"));
        Assert.Equal(32, TestSecrets.Hasher.HashCvv(Pan, "123").Length);    // CardSecret.CvvHash VARBINARY(32)
    }

    [Fact]
    public void Hash_IsDeterministicButDependsOnCardKindValueAndKey()
    {
        var hash = TestSecrets.Hasher.HashCvv(Pan, "123");

        Assert.Equal(hash, TestSecrets.Hasher.HashCvv(Pan, "123"));
        Assert.NotEqual(hash, TestSecrets.Hasher.HashCvv(Pan, "124"));
        Assert.NotEqual(hash, TestSecrets.Hasher.HashCvv("5555444433332222", "123"));    // aynı CVV, başka kart
        Assert.NotEqual(TestSecrets.Hasher.HashCvv(Pan, "1234"), TestSecrets.Hasher.HashPin(Pan, "1234"));  // aynı haneler, CVV / PIN
        Assert.NotEqual(hash, new HmacCardSecretHasher(new string('A', 64)).HashCvv(Pan, "123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00112233445566778899AABBCCDDEEFF")]                                   // 32 hex: eski CVV_KEY uzunluğu
    [InlineData("ZZ112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF")]   // hex değil
    public void Constructor_RejectsKeyThatIsNot64Hex(string key) =>
        Assert.Throws<InvalidOperationException>(() => new HmacCardSecretHasher(key));
}
