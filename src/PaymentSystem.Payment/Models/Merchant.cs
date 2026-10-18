using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Payment.Models;

// dbo.Merchant tablosunun bir satırı (işyeri). İşlem ancak bu tabloda olan ve Active olan bir işyerinde (F42) yapılabilir.
public class Merchant
{
    // F42; sondaki boşluklar olmadan (POS 15 karaktere sağdan boşlukla doldurur).
    [Required, StringLength(15)]
    public string MerchantId { get; set; } = null!;

    [Required, StringLength(100)]
    public string Name { get; set; } = null!;

    // MCC; istekte F18 geldiyse bununla aynı olmalı.
    [Required, RegularExpression("^[0-9]{4}$")]
    public string MerchantType { get; set; } = null!;

    [Required, StringLength(20)]
    public string Status { get; set; } = null!;

    // DB yazar; POST/PUT body'sindeki değer yok sayılır.
    public DateTime CreatedAt { get; set; }
}
