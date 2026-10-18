using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Payment.Models;

// dbo.BankAccount tablosunun bir satırı.
public class BankAccount
{
    [Range(1000000000, 9999999999)]     // 10 hane; ilk hane 0 olamaz (DB'de BIGINT)
    public long AccountNo { get; set; }

    [Required, StringLength(20)]
    public string AccountStatus { get; set; } = null!;

    public decimal Balance { get; set; }

    // Onaylı satışlarda Notification servisi bu adrese e-posta atar; boşsa bildirim gönderilmez.
    [EmailAddress, StringLength(254)]
    public string? Email { get; set; }
}
