using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.Merchant erişimi. Handler'lar bu arayüze bağlıdır; SQL'i MerchantRepository yazar.
public interface IMerchantRepository
{
    Task<IReadOnlyList<Merchant>> GetAllAsync();

    Task<Merchant?> GetAsync(string merchantId);

    Task<WriteResult> CreateAsync(Merchant merchant);

    Task<WriteResult> UpdateAsync(string merchantId, string name, string merchantType, string status);

    Task<bool> DeleteAsync(string merchantId);
}
