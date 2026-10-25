using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/cards")]
public class CardsController(CardHandler cards) : ControllerBase
{
    [EndpointSummary("Tüm kartları listeler")]
    [HttpGet]
    public Task<IReadOnlyList<Card>> GetAll() => cards.GetAllAsync();

    [EndpointSummary("Bir kartı id ile getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Card>> Get(int id) =>
        await cards.GetAsync(id) is { } card ? card : NotFound();

    [EndpointSummary("Bir kartı kart numarasıyla getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("by-number/{cardNumber}")]
    public async Task<ActionResult<Card>> GetByNumber(string cardNumber) =>
        await cards.GetByNumberAsync(cardNumber) is { } card ? card : NotFound();

    // Cevapta kartın CVV2'si ve PIN'i bir kez döner; sonradan hiçbir endpoint göstermez (DB'de sadece hash'leri var).
    [EndpointSummary("Yeni kart basar")]
    [EndpointDescription("Cevapta kartın CVV2'si ve PIN'i bir kez döner; sonradan hiçbir endpoint göstermez.")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<string>(StatusCodes.Status409Conflict, "text/plain")]
    [HttpPost]
    public async Task<ActionResult<IssuedCard>> Create(Card card) =>
        await cards.CreateAsync(card) is (WriteResult.Ok, { } issued)
            ? CreatedAtAction(nameof(Get), new { id = issued.Id }, issued)
            : Conflict($"Card {card.CardNumber} already exists.");

    // Body'deki Id ve LastTransaction* alanları yok sayılır; kart URL'deki id ile bulunur. Kart numarası değiştirilemez.
    [EndpointSummary("Bir kartı günceller")]
    [EndpointDescription("Kart numarası değiştirilemez (400); yeni kart basılmalı.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<string>(StatusCodes.Status400BadRequest, "text/plain")]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Card input) =>
        await cards.UpdateAsync(id, input) switch
        {
            WriteResult.NotFound => NotFound(),
            WriteResult.CardNumberChanged => BadRequest("Card number cannot be changed; issue a new card instead."),
            _ => NoContent()
        };

    [EndpointSummary("Bir kartı siler")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) =>
        await cards.DeleteAsync(id) ? NoContent() : NotFound();
}
