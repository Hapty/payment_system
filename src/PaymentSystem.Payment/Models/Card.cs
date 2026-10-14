using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Payment.Models;

// dbo.Card tablosunun bir satırı.
// CVV ve PIN bu tabloda değil, hash'lenmiş olarak dbo.CardSecret'tadır (CardSecret); düz halleri sadece kart
// oluşturulurken bir kez döner (IssuedCard). Kartın numarası oluşturulduktan sonra değiştirilemez.
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

    // Bu iki alanı işlem akışı yazar; CRUD endpoint'leri sadece okur.
    public DateTime? LastTransactionDate { get; set; }
    public decimal? LastTransactionAmount { get; set; }

    public bool ContactlessAllowed { get; set; }
    public decimal? ContactlessLimit { get; set; }
}
