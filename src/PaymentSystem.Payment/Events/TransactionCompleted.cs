namespace PaymentSystem.Payment.Events;

// Karara bağlanıp commit edilen her işlem için Kafka'ya yazılan olay (onay da red de). Hangi işlemle ilgileneceğine
// tüketici kendisi karar verir: örn. Notification sadece onaylı satışlara e-posta atar.
// Sözleşme JSON'dur (camelCase). Alan eklemek geriye uyumludur; alan silmek / anlamını değiştirmek Version'ı artırır.
// Kart numarası sadece maskeli taşınır (PCI DSS): tam PAN Kafka'ya yazılmaz.
public sealed record TransactionCompleted(
    Guid TransactionId,             // DebitTransaction.Guid; tüketiciler tekrar gelen olayı bununla ayıklar
    DateTimeOffset OccurredAt,      // kararın verildiği an (UTC)
    string Mti,
    string TransactionType,         // TransactionType.Name: Sale, BalanceInquiry ...
    string ResponseCode,            // F39
    string Irc,                     // iç karar kodu
    string MaskedCardNumber,
    long? AccountNo,                // kart bulunamadıysa null
    string? Email,                  // hesabın e-postası; hesap / e-posta yoksa null
    decimal Amount,                 // F4, işlemin kendi para biriminde
    string CurrencyCode,            // F49, ISO 4217 sayısal (949 = TRY)
    decimal? LocalAmount,           // TL karşılığı; kur yoksa null
    string? CardAcceptor,           // F43
    string TransactionDate,         // yyyymmdd
    string LocalTime,               // F12 hhmmss
    string TerminalId,              // F41
    string Stan,                    // F11
    // Sadece para transferinde, alıcı kart bulunduysa (karar ne olursa olsun); diğer işlemlerde null.
    string? RecipientMaskedCardNumber = null,   // F48, maskeli
    long? RecipientAccountNo = null,
    string? RecipientEmail = null)              // alıcı hesabın e-postası; hesap / e-posta yoksa null
{
    public const string Type = nameof(TransactionCompleted);
    public const int CurrentVersion = 1;

    public string EventType { get; init; } = Type;
    public int Version { get; init; } = CurrentVersion;
}
