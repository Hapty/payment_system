namespace PaymentSystem.Payment.Models;

// dbo.CreditTransaction'a açılan yeni kayıt (kredi kartıyla işlem). F39_ResponseCode karar verildikten sonra ayrıca yazılır.
public sealed record CreditTransaction(
    Guid Id,
    string Mti,
    string CardNumber,          // F2
    string ProcessingCode,      // F3
    string Otc,
    string Ots,
    decimal Amount,             // F4, para biriminin kendisiyle
    string CurrencyCode,        // F49
    decimal? LocalAmount,       // TL karşılığı; kur yoksa null
    string LocalTime,           // F12 hhmmss
    string TransactionDate,     // F13, yıl eklenmiş: yyyymmdd
    string? CardExpiry,         // F14
    string? MerchantType,       // F18
    string? EntryMode,          // F22
    string? MerchantId,         // F42, sondaki boşluklar silinmiş
    string? CardAcceptor,       // F43
    int InstallmentCount);      // 1 = tek çekim
