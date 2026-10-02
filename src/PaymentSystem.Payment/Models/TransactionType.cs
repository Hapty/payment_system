using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Payment.Models;

// dbo.TransactionType: (Otc, Ots) kombinasyonunun işlem adı, örn. 10/11 = Sale.
public class TransactionType
{
    [Required, StringLength(2, MinimumLength = 2)]
    public string Otc { get; set; } = null!;

    [Required, StringLength(2, MinimumLength = 2)]
    public string Ots { get; set; } = null!;

    [Required, StringLength(50)]
    public string Name { get; set; } = null!;
}
