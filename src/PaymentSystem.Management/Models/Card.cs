using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Management.Models;

public class Card
{
    public int Id { get; set; }

    [Required, StringLength(19)]
    public string CardNumber { get; set; } = null!;

    [Required, StringLength(34)]
    public string AccountNo { get; set; } = null!;

    [StringLength(20)]
    public string? CardType { get; set; }

    [StringLength(4, MinimumLength = 4)]
    public string? ExpiryDate { get; set; }     // MMYY

    [StringLength(3, MinimumLength = 3)]
    public string? Cvv { get; set; }

    public DateTime? LastTransactionDate { get; set; }
    public decimal? LastTransactionAmount { get; set; }
    public bool ContactlessAllowed { get; set; }
    public decimal? ContactlessLimit { get; set; }
}
