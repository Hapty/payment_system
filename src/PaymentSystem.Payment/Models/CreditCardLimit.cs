using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentSystem.Payment.Models;

// dbo.CreditCardLimit tablosunun bir satırı: bir kredi kartının limiti. Kredi kartıyla satış ancak bu kayıt varsa yapılabilir.
// UsedLimit / UsedCashLimit'i işlem akışı yazar, AvailableLimit'i DB hesaplar; CRUD endpoint'leri bu üçünü sadece okur.
public class CreditCardLimit : IValidatableObject
{
    [Required, StringLength(19)]
    public string CardNumber { get; set; } = null!;

    [JsonRequired, Range(0d, 9_999_999_999_999_999d)]
    public decimal CreditLimit { get; set; }

    public decimal UsedLimit { get; set; }

    public decimal AvailableLimit { get; set; }

    [JsonRequired, Range(0d, 9_999_999_999_999_999d)]
    public decimal CashLimit { get; set; }

    public decimal UsedCashLimit { get; set; }

    // 28'den büyük gün her ayda yok (Şubat).
    [JsonRequired, Range(1, 28)]
    public byte StatementDay { get; set; }

    [JsonRequired, Range(1, 28)]
    public byte DueDay { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CashLimit > CreditLimit)
            yield return new ValidationResult("CashLimit cannot exceed CreditLimit.", [nameof(CashLimit)]);
    }
}
