using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Payment.Models;

// dbo.BankAccount tablosunun bir satırı.
public class BankAccount
{
    [Required, StringLength(34)]
    public string AccountNo { get; set; } = null!;

    [Required, StringLength(20)]
    public string AccountStatus { get; set; } = null!;

    public decimal Balance { get; set; }

    // Onaylı satışlarda Notification servisi bu adrese e-posta atar; boşsa bildirim gönderilmez.
    [EmailAddress, StringLength(254)]
    public string? Email { get; set; }
}
