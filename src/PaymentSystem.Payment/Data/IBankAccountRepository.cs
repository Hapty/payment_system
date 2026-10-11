using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.BankAccount erişimi. Handler'lar bu arayüze bağlıdır; SQL'i BankAccountRepository yazar.
public interface IBankAccountRepository
{
    Task<IReadOnlyList<BankAccount>> GetAllAsync();

    Task<BankAccount?> GetAsync(string accountNo);

    Task<WriteResult> CreateAsync(BankAccount account);

    Task<WriteResult> UpdateStatusAsync(string accountNo, string accountStatus);

    Task<bool> DeleteAsync(string accountNo);
}
