using System.Security.Cryptography;
using System.Text;

namespace PaymentSystem.Payment.Cards;

// HMAC-SHA256(CARD_SECRET_KEY, kart no + ":cvv:" / ":pin:" + değer).
// - Anahtarsız düz hash olmaz: CVV'nin 1000, PIN'in 10.000 olası değeri var, DB sızarsa hepsi saniyeler içinde denenir.
//   Anahtar DB'de değil, ortam değişkenindedir; DB tek başına sızarsa hash'ler bir işe yaramaz.
// - Kart numarası da hesaba girer: aynı CVV'ye sahip iki kartın hash'i farklı olur, DB'den "bu iki kartın CVV'si aynı" okunamaz.
//   Bu yüzden kartın numarası değiştirilemez (bkz. CardHandler.UpdateAsync).
// - ":cvv:" / ":pin:" etiketi aynı haneli bir CVV ile PIN'in aynı hash'i vermesini önler.
public sealed class HmacCardSecretHasher : ICardSecretHasher
{
    private readonly byte[] _key;

    // key: 64 hex hane = 32 bayt (SHA-256 çıktısı kadar; HMAC için önerilen en az uzunluk).
    public HmacCardSecretHasher(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit))
            throw new InvalidOperationException("CARD_SECRET_KEY must be 64 hex characters (32 bytes).");
        _key = Convert.FromHexString(key);
    }

    public byte[] HashCvv(string cardNumber, string cvv) => Hash(cardNumber, "cvv", cvv);

    public byte[] HashPin(string cardNumber, string pin) => Hash(cardNumber, "pin", pin);

    private byte[] Hash(string cardNumber, string kind, string value) =>
        HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{cardNumber}:{kind}:{value}"));
}
