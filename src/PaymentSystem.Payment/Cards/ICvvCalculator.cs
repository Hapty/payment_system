namespace PaymentSystem.Payment.Cards;

// Kartın CVV2'sini (arkasına basılan 3 hane) gizli bir anahtarla kart bilgilerinden hesaplar.
// CVV hiçbir yere kaydedilmez: kart oluşturulurken bir kez hesaplanıp gösterilir, doğrulamada yeniden hesaplanıp karşılaştırılır.
// Anahtarın nerede durduğu (bugün .env, ileride Vault / KMS / HSM) bu arayüzün arkasında kalır.
public interface ICvvCalculator
{
    // expiryDate: Card.ExpiryDate formatında (MMYY). Son kullanma tarihi olmayan kartın CVV'si hesaplanamaz: null.
    string? CalculateCvv2(string cardNumber, string? expiryDate);
}
