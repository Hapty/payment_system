using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;

namespace PaymentSystem.Payment.Handlers;

// MTI + F3 eşlemesi CRUD işlemleri: MtiProcessingCodesController ile MtiProcessingCodeRepository arasında.
// Eşleme değişince ParameterCache boşaltılır: işlem akışı yeni tanımı bir sonraki işlemde görür.
public sealed class MtiProcessingCodeHandler(
    IMtiProcessingCodeRepository codes, ParameterCache parameters, ILogger<MtiProcessingCodeHandler> logger)
{
    public async Task<IReadOnlyList<MtiProcessingCode>> GetAllAsync()
    {
        var all = await codes.GetAllAsync();
        logger.LogInformation("{Method}: returned {Count} mappings", nameof(GetAllAsync), all.Count);
        return all;
    }

    public async Task<MtiProcessingCode?> GetAsync(string mti, string processingCode)
    {
        var code = await codes.GetAsync(mti, processingCode);
        logger.LogInformation("{Method}: mapping {Mti}/{ProcessingCode} {Result}",
            nameof(GetAsync), mti, processingCode, code is null ? "not found" : "found");
        return code;
    }

    public async Task<WriteResult> CreateAsync(MtiProcessingCode code)
    {
        var result = await codes.CreateAsync(code);
        logger.LogInformation("{Method}: mapping {Mti}/{ProcessingCode} -> {Otc}/{Ots} {Result}",
            nameof(CreateAsync), code.Mti, code.F3_ProcessingCode, code.Otc, code.Ots, result);
        if (result == WriteResult.Ok)
            parameters.Invalidate();
        return result;
    }

    // Anahtar (Mti, F3) değişmez; sadece karşılık geldiği (Otc, Ots) güncellenir.
    public async Task<WriteResult> UpdateTargetAsync(string mti, string processingCode, string otc, string ots)
    {
        var result = await codes.UpdateTargetAsync(mti, processingCode, otc, ots);
        logger.LogInformation("{Method}: mapping {Mti}/{ProcessingCode} -> {Otc}/{Ots} {Result}",
            nameof(UpdateTargetAsync), mti, processingCode, otc, ots, result);
        if (result == WriteResult.Ok)
            parameters.Invalidate();
        return result;
    }

    public async Task<bool> DeleteAsync(string mti, string processingCode)
    {
        var deleted = await codes.DeleteAsync(mti, processingCode);
        logger.LogInformation("{Method}: mapping {Mti}/{ProcessingCode} {Result}",
            nameof(DeleteAsync), mti, processingCode, deleted ? "deleted" : "not found");
        if (deleted)
            parameters.Invalidate();
        return deleted;
    }
}
