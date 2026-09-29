using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentSystem.Api.Data;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Controllers;

[ApiController]
[Route("api/cards")]
public class CardsController(PaymentDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<Card>> GetAll() =>
        await db.Cards.AsNoTracking().ToListAsync();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Card>> Get(int id) =>
        await db.Cards.FindAsync(id) is { } card ? card : NotFound();

    [HttpGet("by-number/{cardNumber}")]
    public async Task<ActionResult<Card>> GetByNumber(string cardNumber) =>
        await db.Cards.AsNoTracking().FirstOrDefaultAsync(x => x.CardNumber == cardNumber) is { } card
            ? card
            : NotFound();

    [HttpPost]
    public async Task<ActionResult<Card>> Create(Card card)
    {
        if (await db.Cards.AnyAsync(x => x.CardNumber == card.CardNumber))
            return Conflict($"Card {card.CardNumber} already exists.");

        card.Id = 0; // identity column
        card.LastTransactionDate = null;
        card.LastTransactionAmount = null;
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = card.Id }, card);
    }

    // LastTransactionDate/Amount are written by the transaction (ISO8583) flow, so they are not editable via this endpoint.
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Card input)
    {
        var card = await db.Cards.FindAsync(id);
        if (card is null) return NotFound();

        if (await db.Cards.AnyAsync(x => x.CardNumber == input.CardNumber && x.Id != id))
            return Conflict($"Card {input.CardNumber} already exists.");

        card.CardNumber = input.CardNumber;
        card.AccountNo = input.AccountNo;
        card.CardType = input.CardType;
        card.ExpiryDate = input.ExpiryDate;
        card.Cvv = input.Cvv;
        card.ContactlessAllowed = input.ContactlessAllowed;
        card.ContactlessLimit = input.ContactlessLimit;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var deleted = await db.Cards.Where(x => x.Id == id).ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : NoContent();
    }
}
