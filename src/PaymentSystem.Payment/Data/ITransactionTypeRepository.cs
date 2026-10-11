using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.TransactionType erişimi. Handler'lar bu arayüze bağlıdır; SQL'i TransactionTypeRepository yazar.
public interface ITransactionTypeRepository
{
    Task<IReadOnlyList<TransactionType>> GetAllAsync();

    Task<TransactionType?> GetAsync(string otc, string ots);

    Task<WriteResult> CreateAsync(TransactionType type);

    Task<WriteResult> UpdateNameAsync(string otc, string ots, string name);

    Task<bool> DeleteAsync(string otc, string ots);
}
