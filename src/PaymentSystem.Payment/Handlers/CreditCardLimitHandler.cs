using PaymentSystem.Iso8583;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Handlers;

// Kredi kartı limiti CRUD işlemleri: CreditLimitsController ile CreditCardLimitRepository arasında. Kart numarası loglara maskeli yazılır.
public sealed class CreditCardLimitHandler(ICreditCardLimitRepository limits, ICardRepository cards, ILogger<CreditCardLimitHandler> logger)
{
    public async Task<IReadOnlyList<CreditCardLimit>> GetAllAsync()
    {
        var all = await limits.GetAllAsync();
        logger.LogInformation("{Method}: returned {Count} credit limits", nameof(GetAllAsync), all.Count);
        return all;
    }

    public async Task<CreditCardLimit?> GetAsync(string cardNumber)
    {
        var limit = await limits.GetAsync(cardNumber);
        logger.LogInformation("{Method}: credit limit PAN {Pan} {Result}",
            nameof(GetAsync), PanMask.Mask(cardNumber), limit is null ? "not found" : "found");
        return limit;
    }

    // Limit sadece var olan ve tipi Credit olan karta tanımlanır.
    public async Task<WriteResult> CreateAsync(CreditCardLimit limit)
    {
        var result = await cards.GetByNumberAsync(limit.CardNumber) switch
        {
            null => WriteResult.NotFound,
            { CardType: not CardTypes.Credit } => WriteResult.NotCreditCard,
            _ => await limits.CreateAsync(limit)
        };
        logger.LogInformation("{Method}: credit limit PAN {Pan} limit {CreditLimit} cash {CashLimit} statement day {StatementDay} due day {DueDay} -> {Result}",
            nameof(CreateAsync), PanMask.Mask(limit.CardNumber), limit.CreditLimit, limit.CashLimit, limit.StatementDay, limit.DueDay, result);
        return result;
    }

    public async Task<WriteResult> UpdateAsync(string cardNumber, CreditCardLimit input)
    {
        var result = await limits.UpdateAsync(cardNumber, input);
        logger.LogInformation("{Method}: credit limit PAN {Pan} limit {CreditLimit} cash {CashLimit} statement day {StatementDay} due day {DueDay} -> {Result}",
            nameof(UpdateAsync), PanMask.Mask(cardNumber), input.CreditLimit, input.CashLimit, input.StatementDay, input.DueDay, result);
        return result;
    }

    public async Task<bool> DeleteAsync(string cardNumber)
    {
        var deleted = await limits.DeleteAsync(cardNumber);
        logger.LogInformation("{Method}: credit limit PAN {Pan} {Result}", nameof(DeleteAsync), PanMask.Mask(cardNumber), deleted ? "deleted" : "not found");
        return deleted;
    }
}
