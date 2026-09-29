using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Api.Models;

public class TransactionType
{
    [Required, StringLength(2, MinimumLength = 2)]
    public string Otc { get; set; } = null!;

    [Required, StringLength(2, MinimumLength = 2)]
    public string Ots { get; set; } = null!;

    [Required, StringLength(50)]
    public string Name { get; set; } = null!;
}
