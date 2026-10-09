using System.Xml;
using Microsoft.Extensions.Options;

namespace PaymentSystem.Payment.ExchangeRates;

// Kurları açılışta ve sonra her RefreshMinutes'ta TCMB'den çekip cache'e yazar.
// Çekim başarısız olursa cache'teki son kurlar olduğu gibi kalır ve RetryMinutes sonra tekrar denenir.
public sealed class ExchangeRateRefresher(
    TcmbClient tcmb, ExchangeRateCache rates, IOptions<ExchangeRateOptions> options, TimeProvider clock,
    ILogger<ExchangeRateRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Host, ExecuteAsync'i ilk await'e kadar kendi başlangıcında çalıştırır; uygulamanın açılışı TCMB'yi beklemesin.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var refreshed = await RefreshAsync(stoppingToken);
            var wait = TimeSpan.FromMinutes(refreshed ? options.Value.RefreshMinutes : options.Value.RetryMinutes);
            try
            {
                await Task.Delay(wait, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bulletin = await tcmb.FetchAsync(cancellationToken);
            rates.Store(bulletin);
            logger.LogInformation("Exchange rates refreshed from TCMB bulletin {Date}: {Count} currencies",
                bulletin.Date, bulletin.SellingRates.Count);
            return true;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or XmlException or FormatException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            // Biz iptal etmediğimiz halde TaskCanceledException geldiyse HttpClient.Timeout süresi dolmuştur.
            logger.LogWarning("Could not refresh exchange rates from TCMB ({Error}: {Message}) - keeping the cached rates",
                ex.GetType().Name, ex.Message);
            return false;
        }
    }
}
