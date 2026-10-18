using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Handlers;

// İşyeri CRUD işlemleri: MerchantsController ile MerchantRepository arasında.
// İşlem akışı işyerini cache'ten değil kendi transaction'ında okur: durum değişikliği bir sonraki işlemde geçerlidir.
public sealed class MerchantHandler(IMerchantRepository merchants, ILogger<MerchantHandler> logger)
{
    public async Task<IReadOnlyList<Merchant>> GetAllAsync()
    {
        var all = await merchants.GetAllAsync();
        logger.LogInformation("{Method}: returned {Count} merchants", nameof(GetAllAsync), all.Count);
        return all;
    }

    public async Task<Merchant?> GetAsync(string merchantId)
    {
        var merchant = await merchants.GetAsync(merchantId);
        logger.LogInformation("{Method}: merchant {MerchantId} {Result}", nameof(GetAsync), merchantId, merchant is null ? "not found" : "found");
        return merchant;
    }

    // F42 ile karşılaştırılacağı için no sondaki boşluklar olmadan saklanır.
    public async Task<WriteResult> CreateAsync(Merchant merchant)
    {
        merchant.MerchantId = merchant.MerchantId.TrimEnd();
        var result = await merchants.CreateAsync(merchant);
        logger.LogInformation("{Method}: merchant {MerchantId} {Name} MCC {MerchantType} status {Status} -> {Result}",
            nameof(CreateAsync), merchant.MerchantId, merchant.Name, merchant.MerchantType, merchant.Status, result);
        return result;
    }

    public async Task<WriteResult> UpdateAsync(string merchantId, string name, string merchantType, string status)
    {
        var result = await merchants.UpdateAsync(merchantId, name, merchantType, status);
        logger.LogInformation("{Method}: merchant {MerchantId} {Name} MCC {MerchantType} status {Status} -> {Result}",
            nameof(UpdateAsync), merchantId, name, merchantType, status, result);
        return result;
    }

    public async Task<bool> DeleteAsync(string merchantId)
    {
        var deleted = await merchants.DeleteAsync(merchantId);
        logger.LogInformation("{Method}: merchant {MerchantId} {Result}", nameof(DeleteAsync), merchantId, deleted ? "deleted" : "not found");
        return deleted;
    }
}
