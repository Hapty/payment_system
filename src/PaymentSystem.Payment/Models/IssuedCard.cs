namespace PaymentSystem.Payment.Models;

// POST /api/cards cevabı: kartın kendisi + CVV2'si. CVV sadece burada, kart oluşturulurken bir kez görünür
// (kartın arkasına basılmasının karşılığı); GET'lerde ve DB'de yoktur. Son kullanma tarihi olmayan kartta null.
public sealed class IssuedCard : Card
{
    public string? Cvv { get; init; }

    public static IssuedCard From(Card card, string? cvv) => new()
    {
        Id = card.Id,
        CardNumber = card.CardNumber,
        AccountNo = card.AccountNo,
        CardType = card.CardType,
        ExpiryDate = card.ExpiryDate,
        LastTransactionDate = card.LastTransactionDate,
        LastTransactionAmount = card.LastTransactionAmount,
        ContactlessAllowed = card.ContactlessAllowed,
        ContactlessLimit = card.ContactlessLimit,
        Cvv = cvv
    };
}
