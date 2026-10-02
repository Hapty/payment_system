namespace PaymentSystem.Gate.Payment;

// What the Gate sends to the Payment service: the ISO8583 fields of a financial request under readable names.
// The Gate does not interpret ProcessingCode (sale, balance inquiry, transfer...) - that is the Payment service's job.
public sealed record PaymentRequest(
    string Mti,
    string CardNumber,              // F2
    string ProcessingCode,          // F3
    long AmountMinor,               // F4, e.g. 15000 = 150.00
    string CurrencyCode,            // F49, e.g. 949 = TRY
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
