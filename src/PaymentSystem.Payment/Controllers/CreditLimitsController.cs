using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/credit-limits")]
public class CreditLimitsController(CreditCardLimitHandler limits) : ControllerBase
{
    [EndpointSummary("Tüm kredi kartı limitlerini listeler")]
    [HttpGet]
    public Task<IReadOnlyList<CreditCardLimit>> GetAll() => limits.GetAllAsync();

    [EndpointSummary("Bir kredi kartının limitini kart numarasıyla getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{cardNumber}")]
    public async Task<ActionResult<CreditCardLimit>> Get(string cardNumber) =>
        await limits.GetAsync(cardNumber) is { } limit ? limit : NotFound();

    // Body'deki UsedLimit, AvailableLimit ve UsedCashLimit yok sayılır: yeni limitte kullanılan tutar 0'dır.
    [EndpointSummary("Bir kredi kartına limit tanımlar")]
    [EndpointDescription("Kart yoksa 404, kartın tipi Credit değilse 400, kartın limiti zaten varsa 409.")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<string>(StatusCodes.Status400BadRequest, "text/plain")]
    [ProducesResponseType<string>(StatusCodes.Status409Conflict, "text/plain")]
    [HttpPost]
    public async Task<ActionResult<CreditCardLimit>> Create(CreditCardLimit limit) =>
        await limits.CreateAsync(limit) switch
        {
            WriteResult.NotFound => NotFound(),
            WriteResult.NotCreditCard => BadRequest($"Card {limit.CardNumber} is not a credit card."),
            WriteResult.Duplicate => Conflict($"Credit limit for card {limit.CardNumber} already exists."),
            _ => CreatedAtAction(nameof(Get), new { cardNumber = limit.CardNumber }, limit)
        };

    // Kart URL'den gelir; body'den CreditLimit, CashLimit, StatementDay ve DueDay kullanılır. Kullanılan tutarlar değişmez.
    [EndpointSummary("Bir kredi kartının limitini günceller")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpPut("{cardNumber}")]
    public async Task<IActionResult> Update(string cardNumber, CreditCardLimit input) =>
        await limits.UpdateAsync(cardNumber, input) == WriteResult.NotFound ? NotFound() : NoContent();

    [EndpointSummary("Bir kredi kartının limitini siler")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpDelete("{cardNumber}")]
    public async Task<IActionResult> Delete(string cardNumber) =>
        await limits.DeleteAsync(cardNumber) ? NoContent() : NotFound();
}
