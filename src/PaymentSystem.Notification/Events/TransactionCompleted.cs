namespace PaymentSystem.Notification.Events;

// Payment'ın Kafka'ya yazdığı olay (PaymentSystem.Payment/Events/TransactionCompleted.cs ile aynı JSON, camelCase).
// Notification'ın kullanmadığı alanlar da burada durur; bilinmeyen yeni alanlar okunurken yok sayılır.
public sealed record TransactionCompleted
{
    public string EventType { get; init; } = "";
    public int Version { get; init; }
    public Guid TransactionId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string Mti { get; init; } = "";
    public string TransactionType { get; init; } = "";      // Sale, BalanceInquiry ...
    public string ResponseCode { get; init; } = "";         // F39
    public string Irc { get; init; } = "";
    public string MaskedCardNumber { get; init; } = "";
    public long? AccountNo { get; init; }
    public string? Email { get; init; }
    public decimal Amount { get; init; }                    // işlemin kendi para biriminde
    public string CurrencyCode { get; init; } = "";         // ISO 4217 sayısal
    public decimal? LocalAmount { get; init; }              // TL karşılığı
    public string? CardAcceptor { get; init; }              // F43
    public string TransactionDate { get; init; } = "";      // yyyymmdd
    public string LocalTime { get; init; } = "";            // hhmmss
    public string? TerminalId { get; init; }
    public string? Stan { get; init; }
}
