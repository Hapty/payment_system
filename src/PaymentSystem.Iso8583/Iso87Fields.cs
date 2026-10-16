using static PaymentSystem.Iso8583.IsoCharset;
using static PaymentSystem.Iso8583.IsoLengthType;

namespace PaymentSystem.Iso8583;

// Bu sistemin anladığı ISO 8583:1987 alanları. Burada listelenmeyen bir alan çözülemez,
// çünkü mesaj içinde kaç bayt kapladığı bilinmez.
public static class Iso87Fields
{
    public static IReadOnlyDictionary<int, IsoFieldDefinition> Default { get; } = new IsoFieldDefinition[]
    {
        new(2, "Primary account number (PAN)", LLVar, 19, Numeric, "Kart No"),
        new(3, "Processing code", Fixed, 6, Numeric, "İşlem Kodu"),
        new(4, "Amount, transaction (minor units)", Fixed, 12, Numeric, "Tutar, kuruş"),
        new(7, "Transmission date & time (MMDDhhmmss)", Fixed, 10, Numeric, "Gönderim Zamanı"),
        new(11, "System trace audit number (STAN)", Fixed, 6, Numeric, "STAN"),
        new(12, "Local transaction time (hhmmss)", Fixed, 6, Numeric, "Yerel Saat"),
        new(13, "Local transaction date (MMDD)", Fixed, 4, Numeric, "Yerel Tarih"),
        new(14, "Expiration date (YYMM)", Fixed, 4, Numeric, "Son Kullanma"),
        new(18, "Merchant type", Fixed, 4, Numeric, "İşyeri Tipi"),
        new(22, "POS entry mode", Fixed, 3, Numeric, "Giriş Yöntemi"),
        new(37, "Retrieval reference number", Fixed, 12, AlphaNumeric, "RRN"),
        new(39, "Response code", Fixed, 2, AlphaNumeric, "Cevap Kodu"),
        new(41, "Card acceptor terminal ID", Fixed, 8, Any, "Terminal No"),
        new(42, "Card acceptor ID code", Fixed, 15, Any, "İşyeri No"),
        new(43, "Card acceptor name/location", Fixed, 40, Any, "İşyeri Adı"),
        new(48, "Additional data - private", LLLVar, 999, Any, "Alıcı Kart No"),
        new(49, "Currency code, transaction", Fixed, 3, Numeric, "Para Birimi"),
        new(70, "Network management information code", Fixed, 3, Numeric, "Ağ Yönetim Kodu"),
    }.ToDictionary(f => f.Number);
}
