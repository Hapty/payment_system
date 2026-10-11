using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// Daha önce karara bağlanmış işlemin F39'u ve kartın hesabı (kart artık yoksa null).
public sealed record PreviousResponse(string ResponseCode, string? AccountNo);

// Finansal işlem akışının veritabanı erişimi. Kararları TransactionHandler verir; SQL'i TransactionRepository yazar.
// Tanım tabloları (işlem tipi, F22) buradan değil ParameterCache'ten okunur.
public interface ITransactionRepository
{
    // İşlem kaydı, bakiye ve kart güncellemesi bu oturumdaki tek SQL transaction'ında yapılır.
    Task<IDebitSession> BeginAsync();

    // Tekrar gelen istek için ilk kararın F39'u; kayıt yoksa null.
    Task<PreviousResponse?> GetPreviousAsync(Guid id);
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
