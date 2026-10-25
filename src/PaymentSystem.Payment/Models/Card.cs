using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentSystem.Payment.Models;

// dbo.Card tablosunun bir satırı.
// CVV ve PIN bu tabloda değil, hash'lenmiş olarak dbo.CardSecret'tadır (CardSecret); düz halleri sadece kart
// oluşturulurken bir kez döner (IssuedCard). Kartın numarası oluşturulduktan sonra değiştirilemez.
// Boş kolonlu kart açılmaz; boş kalabilecek tek alanlar LastTransaction* (yeni kartta işlem yok) ve temassıza kapalı
// kartın ContactlessLimit'i (o kart için anlamsız).
public class Card : IValidatableObject
{
    public int Id { get; set; }

    [Required, StringLength(19)]
    public string CardNumber { get; set; } = null!;

    [Range(1000000000, 9999999999)]     // 10 hane; ilk hane 0 olamaz (DB'de BIGINT)
    public long AccountNo { get; set; }

    [Required, StringLength(20)]
    public string? CardType { get; set; }

    [Required, StringLength(4, MinimumLength = 4)]
    public string? ExpiryDate { get; set; }     // MMYY

    // Bu iki alanı işlem akışı yazar; CRUD endpoint'leri sadece okur.
    public DateTime? LastTransactionDate { get; set; }
    public decimal? LastTransactionAmount { get; set; }

    // Body'de olmalı: yoksa sessizce false yazılmaz, istek 400 alır.
    [JsonRequired]
    public bool ContactlessAllowed { get; set; }

    // Temassıza açık kartta zorunlu, kapalı kartta boş olmalı.
    public decimal? ContactlessLimit { get; set; }

    // Active olmayan kartla (Blocked / Cancelled) işlem yapılamaz. Body'de yoksa yeni kart Active açılır.
    [Required, RegularExpression("^(Active|Blocked|Cancelled)$")]
    public string Status { get; set; } = CardStatus.Active;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ContactlessAllowed && ContactlessLimit is null)
            yield return new ValidationResult("ContactlessLimit is required when contactless is allowed.", [nameof(ContactlessLimit)]);
        if (!ContactlessAllowed && ContactlessLimit is not null)
            yield return new ValidationResult("ContactlessLimit must be empty when contactless is not allowed.", [nameof(ContactlessLimit)]);
    }
}

// Card.CardType ve Card.Status değerleri (dbo.Card'daki CHECK ile aynı).
public static class CardStatus
{
    public const string Active = "Active";
}

public static class CardTypes
{
    // Kredi kartı işlemleri banka hesabının bakiyesinden değil, CreditCardLimit'ten düşer. Diğer her tip debit sayılır.
    public const string Credit = "Credit";
}
