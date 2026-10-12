using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PaymentSystem.Payment.ExchangeRates;

// Cache'teki bir kur, GET /api/exchange-rates'in döndüğü şekliyle. IsStale: işlem akışı bu kuru kullanmaz (91).
public sealed record CachedRate(
    string CurrencyCode, string Currency, decimal SellingRate, DateOnly BulletinDate, DateTimeOffset FetchedAt, bool IsStale);

// Kurlar IMemoryCache'te para birimi başına bir kayıt olarak durur ("fx:USD"). Cache'e yazan tek taraf
// ExchangeRateRefresher'dır; işlem akışı kuru sadece buradan okur, TCMB'ye hiç gitmez.
public sealed class ExchangeRateCache(IMemoryCache cache, TimeProvider clock, IOptions<ExchangeRateOptions> options)
{
    // ISO 4217 sayısal kod (F49) -> TCMB'nin harf kodu. Sadece TCMB'nin döviz satış kuru yayımladığı para birimleri.
    private static readonly Dictionary<string, string> IsoToTcmb = new()
    {
        ["840"] = "USD", ["036"] = "AUD", ["208"] = "DKK", ["978"] = "EUR", ["826"] = "GBP", ["756"] = "CHF",
        ["752"] = "SEK", ["124"] = "CAD", ["414"] = "KWD", ["578"] = "NOK", ["682"] = "SAR", ["392"] = "JPY",
        ["946"] = "RON", ["643"] = "RUB", ["156"] = "CNY", ["586"] = "PKR", ["634"] = "QAR", ["410"] = "KRW",
        ["944"] = "AZN", ["784"] = "AED", ["398"] = "KZT"
    };

    private sealed record Entry(decimal SellingRate, DateOnly BulletinDate, DateTimeOffset FetchedAt);

    // Bu para birimi için TCMB'den kur alınabilir mi? Değilse işlem 12 (desteklenmiyor) alır.
    public static bool IsSupported(string currencyCode) => IsoToTcmb.ContainsKey(currencyCode);

    public void Store(TcmbBulletin bulletin)
    {
        var fetchedAt = clock.GetUtcNow();
        foreach (var (code, rate) in bulletin.SellingRates)
            cache.Set(Key(code), new Entry(rate, bulletin.Date, fetchedAt));
    }

    // 1 birim dövizin TL karşılığı. Kur hiç çekilmediyse ya da son başarılı çekimden bu yana MaxAgeHours geçtiyse false.
    public bool TryGetSellingRate(string currencyCode, out decimal rate)
    {
        var cached = Get(currencyCode);
        rate = cached is { IsStale: false } ? cached.SellingRate : 0;
        return cached is { IsStale: false };
    }

    // Sayısal (840) ya da harf (USD, usd) kodla; desteklenmeyen ya da henüz çekilmemiş para biriminde null.
    public CachedRate? Get(string currencyCode)
    {
        var numeric = IsoToTcmb.ContainsKey(currencyCode)
            ? currencyCode
            : IsoToTcmb.FirstOrDefault(p => p.Value.Equals(currencyCode, StringComparison.OrdinalIgnoreCase)).Key;
        if (numeric is null || !cache.TryGetValue(Key(IsoToTcmb[numeric]), out Entry? entry))
            return null;

        var isStale = clock.GetUtcNow() - entry!.FetchedAt > TimeSpan.FromHours(options.Value.MaxAgeHours);
        return new CachedRate(numeric, IsoToTcmb[numeric], entry.SellingRate, entry.BulletinDate, entry.FetchedAt, isStale);
    }

    // Cache'te olan tüm kurlar, harf koduna göre sıralı.
    public IReadOnlyList<CachedRate> GetAll() =>
        IsoToTcmb.Keys.Select(Get).OfType<CachedRate>().OrderBy(r => r.Currency).ToList();

    private static string Key(string tcmbCode) => $"fx:{tcmbCode}";
}
