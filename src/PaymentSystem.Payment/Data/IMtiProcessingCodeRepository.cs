using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.MtiProcessingCode erişimi. Handler'lar bu arayüze bağlıdır; SQL'i MtiProcessingCodeRepository yazar.
public interface IMtiProcessingCodeRepository
{
    Task<IReadOnlyList<MtiProcessingCode>> GetAllAsync();

    Task<MtiProcessingCode?> GetAsync(string mti, string processingCode);

    Task<WriteResult> CreateAsync(MtiProcessingCode code);

    Task<WriteResult> UpdateTargetAsync(string mti, string processingCode, string otc, string ots);

    Task<bool> DeleteAsync(string mti, string processingCode);
}
