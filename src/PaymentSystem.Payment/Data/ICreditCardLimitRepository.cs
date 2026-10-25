using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.CreditCardLimit erişimi. Handler'lar bu arayüze bağlıdır; SQL'i CreditCardLimitRepository yazar.
public interface ICreditCardLimitRepository
{
    Task<IReadOnlyList<CreditCardLimit>> GetAllAsync();

    Task<CreditCardLimit?> GetAsync(string cardNumber);

    Task<WriteResult> CreateAsync(CreditCardLimit limit);

    // Kullanılan tutarlara (UsedLimit, UsedCashLimit) dokunmaz.
    Task<WriteResult> UpdateAsync(string cardNumber, CreditCardLimit input);

    Task<bool> DeleteAsync(string cardNumber);
}
