using PaymentSystem.Iso8583;
using PaymentSystem.Payment.Cards;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Handlers;

// Kart CRUD işlemleri: CardsController ile CardRepository arasında. Kart numarası loglara maskeli yazılır.
public sealed class CardHandler(ICardRepository cards, ICvvCalculator cvv, ILogger<CardHandler> logger)
{
    // Yeni kartın CVV2'si: kartın arkasına basılacak değer. Sadece kart oluşturulurken bir kez döner, hiçbir yere yazılmaz.
    public string? IssueCvv(Card card) => cvv.CalculateCvv2(card.CardNumber, card.ExpiryDate);

    public async Task<IReadOnlyList<Card>> GetAllAsync()
    {
        var all = await cards.GetAllAsync();
        logger.LogInformation("{Method}: returned {Count} cards", nameof(GetAllAsync), all.Count);
        return all;
    }

    public async Task<Card?> GetAsync(int id)
    {
        var card = await cards.GetAsync(id);
        logger.LogInformation("{Method}: card {Id} {Result}", nameof(GetAsync), id, card is null ? "not found" : "found");
        return card;
    }

    public async Task<Card?> GetByNumberAsync(string cardNumber)
    {
        var card = await cards.GetByNumberAsync(cardNumber);
        logger.LogInformation("{Method}: card PAN {Pan} {Result}",
            nameof(GetByNumberAsync), PanMask.Mask(cardNumber), card is null ? "not found" : "found");
        return card;
    }

    public async Task<WriteResult> CreateAsync(Card card)
    {
        var result = await cards.CreateAsync(card);
        logger.LogInformation("{Method}: card PAN {Pan} account {AccountNo} -> {Result} (id {Id})",
            nameof(CreateAsync), PanMask.Mask(card.CardNumber), card.AccountNo, result, card.Id);
        return result;
    }

    public async Task<WriteResult> UpdateAsync(int id, Card input)
    {
        var result = await cards.UpdateAsync(id, input);
        logger.LogInformation("{Method}: card {Id} PAN {Pan} -> {Result}", nameof(UpdateAsync), id, PanMask.Mask(input.CardNumber), result);
        return result;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var deleted = await cards.DeleteAsync(id);
        logger.LogInformation("{Method}: card {Id} {Result}", nameof(DeleteAsync), id, deleted ? "deleted" : "not found");
        return deleted;
    }
}
