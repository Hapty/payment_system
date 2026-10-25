using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace PaymentSystem.Payment.Models;

// Gate'in POST /api/transactions ile gönderdiği finansal istek (Gate'teki PaymentRequest ile aynı JSON).
// Alanların ISO8583 formatını Gate zaten kontrol eder; buradaki kurallar DB kolonlarına sığmayan değerleri durdurur.
public class TransactionRequest
{
    [Required, StringLength(4, MinimumLength = 4)]
    public string Mti { get; set; } = null!;

    [Required, StringLength(19)]
    public string CardNumber { get; set; } = null!;             // F2

    [Required, StringLength(6, MinimumLength = 6)]
    public string ProcessingCode { get; set; } = null!;         // F3

    [Range(0, 999_999_999_999)]
    public long AmountMinor { get; set; }                       // F4, kuruş cinsinden: 15000 = 150,00

    [Required, StringLength(3, MinimumLength = 3)]
    public string CurrencyCode { get; set; } = null!;           // F49

    [Required, StringLength(10, MinimumLength = 10)]
    public string TransmissionDateTime { get; set; } = null!;   // F7 MMDDhhmmss

    [Required, StringLength(6, MinimumLength = 6)]
    public string Stan { get; set; } = null!;                   // F11

    [Required, StringLength(6, MinimumLength = 6)]
    public string LocalTime { get; set; } = null!;              // F12 hhmmss

    [Required, RegularExpression("^[0-9]{4}(0[1-9]|1[0-2])(0[1-9]|[12][0-9]|3[01])$")]
    public string LocalDate { get; set; } = null!;              // F13 yyyymmdd (Gate yılıyla birlikte gönderir)

    [Required, StringLength(8)]
    public string TerminalId { get; set; } = null!;             // F41

    [StringLength(4, MinimumLength = 4)]
    public string? CardExpiry { get; set; }                     // F14 YYMM

    [StringLength(4)]
    public string? MerchantType { get; set; }                   // F18

    [StringLength(3, MinimumLength = 3)]
    public string? EntryMode { get; set; }                      // F22

    [StringLength(12)]
    public string? Rrn { get; set; }                            // F37

    [StringLength(15)]
    public string? MerchantId { get; set; }                     // F42

    [StringLength(40)]
    public string? CardAcceptor { get; set; }                   // F43

    // F48. Para transferinde alıcı kart no; formatını TransactionHandler kontrol eder (diğer işlemlerde kullanılmaz).
    [StringLength(999)]
    public string? RecipientCardNumber { get; set; }

    // CVV2 (kartın arkası). ISO8583:1987'de standart alanı yok; Gate ayrı bir alan olarak iletir. Asla loglanmaz / saklanmaz.
    [RegularExpression("^[0-9]{3,4}$")]
    public string? Cvv { get; set; }

    // Taksit sayısı; ISO8583:1987'de standart alanı yok, Gate ayrı bir alan olarak iletir. Yoksa tek çekim (1).
    // Taksit sadece kredi kartıyla satışta olur; başka işlemde 1'den büyükse TransactionHandler reddeder.
    [Range(1, 36)]
    public int? InstallmentCount { get; set; }

    // Loglar için alan numarasıyla (bkz. IsoLog). CVV ISO alanı değildir, burada yoktur.
    public IEnumerable<KeyValuePair<int, string?>> IsoFields() =>
    [
        new(2, CardNumber), new(3, ProcessingCode), new(4, AmountMinor.ToString(CultureInfo.InvariantCulture)),
        new(7, TransmissionDateTime), new(11, Stan), new(12, LocalTime), new(13, LocalDate), new(14, CardExpiry),
        new(18, MerchantType), new(22, EntryMode), new(37, Rrn), new(41, TerminalId), new(42, MerchantId),
        new(43, CardAcceptor), new(48, RecipientCardNumber), new(49, CurrencyCode)
    ];
}

// Sonuç ne olursa olsun (onay ya da red) 200 ile döner; Gate ResponseCode'u F39'a yazar.
public sealed record TransactionResponse(string ResponseCode);
