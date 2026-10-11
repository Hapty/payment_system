using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// MTI + F3'ün yönlendirildiği işlem tipi, örn. 0200 + 000000 -> 10/11 Sale.
public sealed record RoutedType(string Otc, string Ots, string Name);

// Finansal işlem akışının veritabanı erişimi. Kararları TransactionHandler verir; SQL'i TransactionRepository yazar.
public interface ITransactionRepository
{
    Task<RoutedType?> FindTypeAsync(string mti, string processingCode);

    // İşlem kaydı, bakiye ve kart güncellemesi bu oturumdaki tek SQL transaction'ında yapılır.
    Task<IDebitSession> BeginAsync();

    // Daha önce karara bağlanmış işlemin F39'u (tekrar gelen istek için).
    Task<string?> GetResponseCodeAsync(Guid id);
}

// Açık bir SQL transaction'ı. CommitAsync çağrılmadan dispose edilirse her şey geri alınır.
public interface IDebitSession : IAsyncDisposable
{
    // false: aynı Guid'li kayıt zaten var (tekrar gelen istek). Aynı anda gelirse ilk transaction bitene kadar bekler.
    Task<bool> TryInsertAsync(DebitTransaction transaction);

    Task<Card?> GetCardAsync(string cardNumber);

    Task<string?> GetAccountStatusAsync(string accountNo);

    // Bakiye kontrolü ve düşme tek sorguda; false: bakiye yetersiz.
    Task<bool> TryDebitAsync(string accountNo, decimal amount);

    Task SetCardLastTransactionAsync(string cardNumber, decimal amount);

    Task SetResponseCodeAsync(Guid id, string responseCode);

    Task CommitAsync();
}
