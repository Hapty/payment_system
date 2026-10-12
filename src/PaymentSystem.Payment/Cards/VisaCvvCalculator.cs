using System.Security.Cryptography;

namespace PaymentSystem.Payment.Cards;

// Visa'nın CVV algoritması (Mastercard'ın CVC'si de aynı): bankalar CVV'yi bu şekilde, CVK adlı bir anahtarla üretir.
//   1. PAN + son kullanma (YYMM) + servis kodu yan yana yazılır, sağdan sıfırla 32 haneye tamamlanır; 16'şar haneli iki blok.
//   2. Blok 1 KeyA ile DES'le şifrelenir, sonuç blok 2 ile XOR'lanır.
//   3. Sonuç KeyA + KeyB ile 3DES'le (EDE) şifrelenir.
//   4. Çıkan 16 hex haneden önce rakamlar, yetmezse A-F (0-5'e çevrilerek) soldan alınır; ilk 3 hane CVV'dir.
// CVV2 (kartın arkası) için servis kodu 000'dır.
// Gerçek bankalarda bu hesap HSM'in içinde yapılır ve anahtar HSM'den çıkmaz; burada anahtar CVV_KEY ortam değişkenindedir.
public sealed class VisaCvvCalculator : ICvvCalculator
{
    private const string Cvv2ServiceCode = "000";

    private readonly byte[] _keyA;
    private readonly byte[] _keyB;

    // key: 32 hex hane = KeyA (16) + KeyB (16).
    public VisaCvvCalculator(string key)
    {
        if (key.Length != 32 || !key.All(Uri.IsHexDigit))
            throw new InvalidOperationException("CVV_KEY must be 32 hex characters (KeyA + KeyB, 16 bytes).");
        var bytes = Convert.FromHexString(key);
        (_keyA, _keyB) = (bytes[..8], bytes[8..]);
        if (_keyA.AsSpan().SequenceEqual(_keyB))
            throw new InvalidOperationException("CVV_KEY halves must differ (KeyA != KeyB).");

        KeyCheckValue = Convert.ToHexString(TripleDesEncrypt(new byte[8]))[..6];
    }

    // Anahtarın parmak izi (KCV): sıfır bloğun 3DES şifresinin ilk 6 hanesi. Anahtarı göstermeden doğru yüklendiğini gösterir.
    public string KeyCheckValue { get; }

    public string? CalculateCvv2(string cardNumber, string? expiryDate) =>
        expiryDate is { Length: 4 } mmyy && cardNumber.All(char.IsAsciiDigit)
            ? Calculate(cardNumber, yymm: mmyy[2..] + mmyy[..2], Cvv2ServiceCode)
            : null;

    public string Calculate(string cardNumber, string yymm, string serviceCode)
    {
        var data = (cardNumber + yymm + serviceCode).PadRight(32, '0');
        var block1 = Convert.FromHexString(data[..16]);
        var block2 = Convert.FromHexString(data[16..32]);

        byte[] result;
        using (var des = DES.Create())
        {
            des.Key = _keyA;
            result = des.EncryptEcb(block1, PaddingMode.None);
        }
        for (var i = 0; i < result.Length; i++)
            result[i] ^= block2[i];
        var hex = Convert.ToHexString(TripleDesEncrypt(result));

        var digits = hex.Where(char.IsAsciiDigit)
            .Concat(hex.Where(c => c is >= 'A' and <= 'F').Select(c => (char)('0' + (c - 'A'))));
        return new string(digits.Take(3).ToArray());
    }

    private byte[] TripleDesEncrypt(byte[] block)
    {
        using var tdes = TripleDES.Create();
        // 2 anahtarlı 3DES: KeyA ile şifrele, KeyB ile çöz, KeyA ile şifrele. 24 bayt olarak verilir (KeyA KeyB KeyA):
        // aynı işlemdir, ama Windows'un CNG'si 16 baytlık anahtarı kabul etmez.
        tdes.Key = [.. _keyA, .. _keyB, .. _keyA];
        return tdes.EncryptEcb(block, PaddingMode.None);
    }
}
