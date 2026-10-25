using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// Daha önce karara bağlanmış işlemin F39'u ve kartın hesabı (kart artık yoksa null).
public sealed record PreviousResponse(string ResponseCode, long? AccountNo);

// Finansal işlem akışının veritabanı erişimi. Kararları TransactionHandler verir; SQL'i TransactionRepository yazar.
// Tanım tabloları (işlem tipi, F22) buradan değil ParameterCache'ten okunur.
public interface ITransactionRepository
{
    // İşlem kaydı, bakiye ve kart güncellemesi bu oturumdaki tek SQL transaction'ında yapılır.
    Task<ITransactionSession> BeginAsync();

    // Tekrar gelen istek için ilk kararın F39'u (DebitTransaction ya da CreditTransaction'da); kayıt yoksa null.
    Task<PreviousResponse?> GetPreviousAsync(Guid id);
}

// Açık bir SQL transaction'ı. CommitAsync çağrılmadan dispose edilirse her şey geri alınır.
public interface ITransactionSession : IAsyncDisposable
{
    // false: aynı Guid'li kayıt zaten var (tekrar gelen istek). Aynı anda gelirse ilk transaction bitene kadar bekler.
    // Oturumda tek kayıt açılır: debit kartın işlemi DebitTransaction'a, kredi kartınınki CreditTransaction'a.
    Task<bool> TryInsertAsync(DebitTransaction transaction);

    Task<bool> TryInsertAsync(CreditTransaction transaction);

    // F42 ile işyeri; tabloda yoksa null.
    Task<Merchant?> GetMerchantAsync(string merchantId);

    Task<Card?> GetCardAsync(string cardNumber);

    // dbo.CardSecret'taki CVV2 hash'i; kartın CardSecret satırı yoksa null.
    Task<byte[]?> GetCvvHashAsync(string cardNumber);

    // Durum ve e-posta (bildirim olayı için); bakiye okunmaz, TryDebitAsync'te kontrol edilir.
    Task<BankAccount?> GetAccountAsync(long accountNo);

    // Bakiye kontrolü ve düşme tek sorguda; false: bakiye yetersiz.
    Task<bool> TryDebitAsync(long accountNo, decimal amount);

    // Para transferi: gönderenden düşme ve alıcıya ekleme bu transaction'da birlikte; false: gönderenin bakiyesi yetersiz.
    Task<bool> TryTransferAsync(long fromAccountNo, long toAccountNo, decimal amount);

    // Kredi kartının limiti; kayıt yoksa null. Kullanılabilir limit okunmaz, TryUseCreditLimitAsync'te kontrol edilir.
    Task<CreditCardLimit?> GetCreditLimitAsync(string cardNumber);

    // Kullanılabilir limit kontrolü ve UsedLimit'in artması tek sorguda; false: limit yetersiz.
    Task<bool> TryUseCreditLimitAsync(string cardNumber, decimal amount);

    Task SetCardLastTransactionAsync(string cardNumber, decimal amount);

    // Bu oturumda açılan kaydın (debit ya da kredi) F39'u.
    Task SetResponseCodeAsync(Guid id, string responseCode);

    Task CommitAsync();
}
