using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.ExchangeRates;

namespace PaymentSystem.Payment.Controllers;

// Kur cache'inin içeriği (salt okunur). Kurları sadece ExchangeRateRefresher yazar; bu endpoint TCMB'ye gitmez.
[ApiController]
[Route("api/exchange-rates")]
public class ExchangeRatesController(ExchangeRateCache rates) : ControllerBase
{
    // Cache boşsa (Payment yeni açıldı, TCMB'ye henüz ulaşılamadı) boş liste döner.
    [HttpGet]
    public IReadOnlyList<CachedRate> GetAll() => rates.GetAll();

    // {currencyCode}: ISO 4217 sayısal (840) ya da harf (USD) kod.
    [HttpGet("{currencyCode}")]
    public ActionResult<CachedRate> Get(string currencyCode) =>
        rates.Get(currencyCode) is { } rate ? rate : NotFound();
}
