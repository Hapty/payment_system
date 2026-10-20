using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Handlers;

namespace PaymentSystem.Payment.Controllers;

// Kur cache'inin içeriği (salt okunur). Kurları sadece ExchangeRateRefresher yazar; bu endpoint TCMB'ye gitmez.
[ApiController]
[Route("api/exchange-rates")]
public class ExchangeRatesController(ExchangeRateHandler rates) : ControllerBase
{
    // Cache boşsa (Payment yeni açıldı, TCMB'ye henüz ulaşılamadı) boş liste döner.
    [EndpointSummary("Cache'teki tüm döviz kurlarını listeler")]
    [HttpGet]
    public IReadOnlyList<CachedRate> GetAll() => rates.GetAll();

    // {currencyCode}: ISO 4217 sayısal (840) ya da harf (USD) kod.
    [EndpointSummary("Bir döviz kurunu getirir")]
    [EndpointDescription("{currencyCode}: ISO 4217 sayısal (840) ya da harf (USD) kod.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{currencyCode}")]
    public ActionResult<CachedRate> Get(string currencyCode) =>
        rates.Get(currencyCode) is { } rate ? rate : NotFound();
}
