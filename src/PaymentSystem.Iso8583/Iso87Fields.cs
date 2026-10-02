using static PaymentSystem.Iso8583.IsoCharset;
using static PaymentSystem.Iso8583.IsoLengthType;

namespace PaymentSystem.Iso8583;

// Bu sistemin anladığı ISO 8583:1987 alanları. Burada listelenmeyen bir alan çözülemez,
// çünkü mesaj içinde kaç bayt kapladığı bilinmez.
public static class Iso87Fields
{
    public static IReadOnlyDictionary<int, IsoFieldDefinition> Default { get; } = new IsoFieldDefinition[]
    {
        new(2, "Primary account number (PAN)", LLVar, 19, Numeric),
        new(3, "Processing code", Fixed, 6, Numeric),
        new(4, "Amount, transaction (minor units)", Fixed, 12, Numeric),
        new(7, "Transmission date & time (MMDDhhmmss)", Fixed, 10, Numeric),
        new(11, "System trace audit number (STAN)", Fixed, 6, Numeric),
        new(12, "Local transaction time (hhmmss)", Fixed, 6, Numeric),
        new(13, "Local transaction date (MMDD)", Fixed, 4, Numeric),
        new(14, "Expiration date (YYMM)", Fixed, 4, Numeric),
        new(18, "Merchant type", Fixed, 4, Numeric),
        new(22, "POS entry mode", Fixed, 3, Numeric),
        new(37, "Retrieval reference number", Fixed, 12, AlphaNumeric),
        new(39, "Response code", Fixed, 2, AlphaNumeric),
        new(41, "Card acceptor terminal ID", Fixed, 8, Any),
        new(42, "Card acceptor ID code", Fixed, 15, Any),
        new(43, "Card acceptor name/location", Fixed, 40, Any),
        new(49, "Currency code, transaction", Fixed, 3, Numeric),
        new(70, "Network management information code", Fixed, 3, Numeric),
    }.ToDictionary(f => f.Number);
}
