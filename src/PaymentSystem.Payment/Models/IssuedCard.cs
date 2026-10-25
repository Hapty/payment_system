namespace PaymentSystem.Payment.Models;

// POST /api/cards cevabı: kartın kendisi + CVV2'si ve PIN'i. İkisi de sadece burada, kart oluşturulurken bir kez görünür
// (kartın arkasına basılmasının / PIN zarfının karşılığı); GET'lerde yoktur, DB'de sadece hash'leri durur (dbo.CardSecret).
public sealed class IssuedCard : Card
{
    public required string Cvv { get; init; }
    public required string Pin { get; init; }

    public static IssuedCard From(Card card, string cvv, string pin) => new()
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
        Status = card.Status,
        Cvv = cvv,
        Pin = pin
    };
}
