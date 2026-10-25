using System.Security.Cryptography;
using PaymentSystem.Iso8583;
using PaymentSystem.Payment.Cards;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Handlers;

// Kart CRUD işlemleri: CardsController ile CardRepository arasında. Kart numarası loglara maskeli yazılır,
// CVV ve PIN hiçbir loga yazılmaz.
public sealed class CardHandler(ICardRepository cards, ICardSecretHasher secrets, ILogger<CardHandler> logger)
{

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

    // Yeni kartın CVV2'si (3 hane) ve PIN'i (4 hane) rastgele üretilir; DB'ye sadece hash'leri yazılır.
    // Düz halleri sadece burada, bir kez döner (kartın arkasına basılan değer / PIN zarfı). Duplicate'te Issued null.
    public async Task<(WriteResult Result, IssuedCard? Issued)> CreateAsync(Card card)
    {
        var cvv = RandomDigits(3);
        var pin = RandomDigits(4);
        var result = await cards.CreateAsync(card,
            new CardSecret(card.CardNumber, secrets.HashCvv(card.CardNumber, cvv), secrets.HashPin(card.CardNumber, pin)));
        logger.LogInformation("{Method}: card PAN {Pan} account {AccountNo} -> {Result} (id {Id})",
            nameof(CreateAsync), PanMask.Mask(card.CardNumber), card.AccountNo, result, card.Id);
        return (result, result == WriteResult.Ok ? IssuedCard.From(card, cvv, pin) : null);
    }

    // Kart numarası değiştirilemez: CVV / PIN hash'i numaraya bağlı, numara değişirse ikisi de geçersiz kalır.
    // Gerçek bankalarda da numara değişince yeni kart çıkarılır (POST).
    public async Task<WriteResult> UpdateAsync(int id, Card input)
    {
        var result = await cards.GetAsync(id) switch
        {
            null => WriteResult.NotFound,
            { } existing when existing.CardNumber != input.CardNumber => WriteResult.CardNumberChanged,
            _ => await cards.UpdateAsync(id, input)
        };
        logger.LogInformation("{Method}: card {Id} PAN {Pan} -> {Result}", nameof(UpdateAsync), id, PanMask.Mask(input.CardNumber), result);
        return result;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var deleted = await cards.DeleteAsync(id);
        logger.LogInformation("{Method}: card {Id} {Result}", nameof(DeleteAsync), id, deleted ? "deleted" : "not found");
        return deleted;
    }

    // Kriptografik rastgele, baştaki sıfırlar korunur (örn. "007").
    private static string RandomDigits(int length) =>
        RandomNumberGenerator.GetInt32((int)Math.Pow(10, length)).ToString($"D{length}");
}
