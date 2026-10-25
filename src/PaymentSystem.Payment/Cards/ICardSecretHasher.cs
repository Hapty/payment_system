namespace PaymentSystem.Payment.Cards;

// Kartın CVV2'sini ve PIN'ini dbo.CardSecret'a yazılacak hale getirir: geri çözülemeyen, anahtarlı bir özet (hash).
// Doğrulamada gelen değer aynı şekilde hash'lenip saklananla karşılaştırılır; düz CVV / PIN hiçbir yere yazılmaz.
// Anahtarın nerede durduğu (bugün .env, ileride Vault / KMS / HSM) bu arayüzün arkasında kalır.
public interface ICardSecretHasher
{
    byte[] HashCvv(string cardNumber, string cvv);

    byte[] HashPin(string cardNumber, string pin);
}
