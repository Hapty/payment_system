using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;

namespace PaymentSystem.Payment.Handlers;

// İşlem tipi CRUD işlemleri: TransactionTypesController ile TransactionTypeRepository arasında.
// İşlem tipi değişince ParameterCache boşaltılır: işlem akışı yeni tanımı bir sonraki işlemde görür.
public sealed class TransactionTypeHandler(
    ITransactionTypeRepository types, ParameterCache parameters, ILogger<TransactionTypeHandler> logger)
{
    public async Task<IReadOnlyList<TransactionType>> GetAllAsync()
    {
        var all = await types.GetAllAsync();
        logger.LogInformation("{Method}: returned {Count} transaction types", nameof(GetAllAsync), all.Count);
        return all;
    }

    public async Task<TransactionType?> GetAsync(string otc, string ots)
    {
        var type = await types.GetAsync(otc, ots);
        logger.LogInformation("{Method}: transaction type {Otc}/{Ots} {Result}", nameof(GetAsync), otc, ots, type is null ? "not found" : "found");
        return type;
    }

    public async Task<WriteResult> CreateAsync(TransactionType type)
    {
        var result = await types.CreateAsync(type);
        logger.LogInformation("{Method}: transaction type {Otc}/{Ots} {Name} -> {Result}",
            nameof(CreateAsync), type.Otc, type.Ots, type.Name, result);
        if (result == WriteResult.Ok)
            parameters.Invalidate();
        return result;
    }

    // Anahtar (Otc, Ots) değişmez; sadece isim güncellenir.
    public async Task<WriteResult> UpdateNameAsync(string otc, string ots, string name)
    {
        var result = await types.UpdateNameAsync(otc, ots, name);
        logger.LogInformation("{Method}: transaction type {Otc}/{Ots} name {Name} -> {Result}",
            nameof(UpdateNameAsync), otc, ots, name, result);
        if (result == WriteResult.Ok)
            parameters.Invalidate();
        return result;
    }

    public async Task<bool> DeleteAsync(string otc, string ots)
    {
        var deleted = await types.DeleteAsync(otc, ots);
        logger.LogInformation("{Method}: transaction type {Otc}/{Ots} {Result}",
            nameof(DeleteAsync), otc, ots, deleted ? "deleted" : "not found");
        if (deleted)
            parameters.Invalidate();
        return deleted;
    }
}
