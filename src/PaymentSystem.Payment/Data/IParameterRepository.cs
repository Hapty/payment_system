namespace PaymentSystem.Payment.Data;

// MTI + F3'ün yönlendirildiği işlem tipi, örn. 0200 + 000000 -> 10/11 Sale.
public sealed record RoutedType(string Otc, string Ots, string Name);

// dbo.MtiProcessingCode + dbo.TransactionType birleşiminin bir satırı: (Mti, F3) -> (Otc, Ots, Name).
public sealed record TransactionRoute(string Mti, string ProcessingCode, string Otc, string Ots, string Name);

// dbo.EntryMode satırı: F22'nin ilk iki hanesi -> giriş yöntemi, örn. 07 -> ContactlessChip (temassız).
public sealed record EntryMode(string Code, string Name, bool IsContactless);

// İşlem akışının tanım (parametre) tabloları. Tamamı birden okunur ve ParameterCache'te tutulur.
public interface IParameterRepository
{
    Task<IReadOnlyList<TransactionRoute>> GetRoutesAsync();

    Task<IReadOnlyList<EntryMode>> GetEntryModesAsync();
}
