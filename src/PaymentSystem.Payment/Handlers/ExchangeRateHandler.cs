using PaymentSystem.Payment.ExchangeRates;

namespace PaymentSystem.Payment.Handlers;

// Kur cache'ini okur (salt okunur): ExchangeRatesController ile ExchangeRateCache arasında.
// Kurların deposu DB değil cache'tir; cache'e sadece ExchangeRateRefresher yazar.
public sealed class ExchangeRateHandler(ExchangeRateCache rates, ILogger<ExchangeRateHandler> logger)
{
    public IReadOnlyList<CachedRate> GetAll()
    {
        var all = rates.GetAll();
        logger.LogInformation("{Method}: returned {Count} cached rates", nameof(GetAll), all.Count);
        return all;
    }

    public CachedRate? Get(string currencyCode)
    {
        var rate = rates.Get(currencyCode);
        logger.LogInformation("{Method}: rate {CurrencyCode} {Result}", nameof(Get), currencyCode,
            rate is null ? "not cached" : $"{rate.SellingRate} (stale: {rate.IsStale})");
        return rate;
    }
}
