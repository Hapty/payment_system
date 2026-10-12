using System.ComponentModel.DataAnnotations;

namespace PaymentSystem.Payment.Models;

// dbo.MtiProcessingCode: gelen mesajın MTI + F3 değerinin hangi (Otc, Ots)'ye karşılık geldiği, örn. 0200 + 000000 -> 10/11.
public class MtiProcessingCode
{
    [Required, StringLength(4, MinimumLength = 4)]
    public string Mti { get; set; } = null!;

    [Required, StringLength(6, MinimumLength = 6)]
    public string F3_ProcessingCode { get; set; } = null!;

    [Required, StringLength(2, MinimumLength = 2)]
    public string Otc { get; set; } = null!;

    [Required, StringLength(2, MinimumLength = 2)]
    public string Ots { get; set; } = null!;
}
