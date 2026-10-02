namespace PaymentSystem.Gate.Payment;

// Gate'in Payment servisine gönderdiği veri: bir finansal isteğin ISO8583 alanları, okunabilir isimlerle.
// Gate ProcessingCode'u (satış, bakiye sorgu, transfer...) yorumlamaz; bu Payment servisinin işidir.
public sealed record PaymentRequest(
    string Mti,
    string CardNumber,              // F2
    string ProcessingCode,          // F3
    long AmountMinor,               // F4, kuruş cinsinden: 15000 = 150,00
    string CurrencyCode,            // F49, örn. 949 = TRY
    string TransmissionDateTime,    // F7  MMDDhhmmss
    string Stan,                    // F11
    string LocalTime,               // F12 hhmmss
    string LocalDate,               // F13 MMDD
    string TerminalId,              // F41
    string? CardExpiry,             // F14 YYMM
    string? MerchantType,           // F18
    string? EntryMode,              // F22
    string? Rrn,                    // F37
    string? MerchantId,             // F42
    string? CardAcceptor);          // F43

public sealed record PaymentResponse(string ResponseCode);
