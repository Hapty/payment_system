using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/cards")]
public class CardsController(CardRepository cards) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<Card>> GetAll() => cards.GetAllAsync();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Card>> Get(int id) =>
        await cards.GetAsync(id) is { } card ? card : NotFound();

    [HttpGet("by-number/{cardNumber}")]
    public async Task<ActionResult<Card>> GetByNumber(string cardNumber) =>
        await cards.GetByNumberAsync(cardNumber) is { } card ? card : NotFound();

    [HttpPost]
    public async Task<ActionResult<Card>> Create(Card card) =>
        await cards.CreateAsync(card) == WriteResult.Duplicate
            ? Conflict($"Card {card.CardNumber} already exists.")
            : CreatedAtAction(nameof(Get), new { id = card.Id }, card);

    // Body'deki Id ve LastTransaction* alanları yok sayılır; kart URL'deki id ile bulunur.
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Card input) =>
        await cards.UpdateAsync(id, input) switch
        {
            WriteResult.NotFound => NotFound(),
            WriteResult.Duplicate => Conflict($"Card {input.CardNumber} already exists."),
            _ => NoContent()
        };

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) =>
        await cards.DeleteAsync(id) ? NoContent() : NotFound();
}
