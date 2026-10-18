using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.BankAccount erişimi. Handler'lar bu arayüze bağlıdır; SQL'i BankAccountRepository yazar.
public interface IBankAccountRepository
{
    Task<IReadOnlyList<BankAccount>> GetAllAsync();

    Task<BankAccount?> GetAsync(long accountNo);

    Task<WriteResult> CreateAsync(BankAccount account);

    Task<WriteResult> UpdateAsync(long accountNo, string accountStatus, string? email);

    Task<bool> DeleteAsync(long accountNo);
}
