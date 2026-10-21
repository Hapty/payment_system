using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentSystem.Payment.Models;

// dbo.BankAccount tablosunun bir satırı. Boş kolonlu hesap açılmaz: her alan zorunludur.
public class BankAccount
{
    [Range(1000000000, 9999999999)]     // 10 hane; ilk hane 0 olamaz (DB'de BIGINT)
    public long AccountNo { get; set; }

    [Required, StringLength(20)]
    public string AccountStatus { get; set; } = null!;

    // Body'de olmalı (0 olabilir): yoksa sessizce 0 yazılmaz, istek 400 alır.
    [JsonRequired]
    public decimal Balance { get; set; }

    // Onaylı satış ve transferlerde Notification servisi bu adrese e-posta atar.
    [Required, EmailAddress, StringLength(254)]
    public string? Email { get; set; }
}
