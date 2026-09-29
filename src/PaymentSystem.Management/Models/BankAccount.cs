using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Management.Models;

public class BankAccount
{
    [Required, StringLength(34)]
    public string AccountNo { get; set; } = null!;

    [Required, StringLength(20)]
    public string AccountStatus { get; set; } = null!;

    public decimal Balance { get; set; }
}
