using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/transaction-types")]
public class TransactionTypesController(TransactionTypeHandler types) : ControllerBase
{
    [EndpointSummary("Tüm işlem tiplerini listeler")]
    [HttpGet]
    public Task<IReadOnlyList<TransactionType>> GetAll() => types.GetAllAsync();

    [EndpointSummary("Bir işlem tipini OTC/OTS ile getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{otc}/{ots}")]
    public async Task<ActionResult<TransactionType>> Get(string otc, string ots) =>
        await types.GetAsync(otc, ots) is { } type ? type : NotFound();

    [EndpointSummary("Yeni işlem tipi oluşturur")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<string>(StatusCodes.Status409Conflict, "text/plain")]
    [HttpPost]
    public async Task<ActionResult<TransactionType>> Create(TransactionType type) =>
        await types.CreateAsync(type) == WriteResult.Duplicate
            ? Conflict($"Transaction type {type.Otc}/{type.Ots} already exists.")
            : CreatedAtAction(nameof(Get), new { otc = type.Otc, ots = type.Ots }, type);

    // Anahtar URL'den gelir; body'den sadece Name kullanılır.
    [EndpointSummary("Bir işlem tipini günceller")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpPut("{otc}/{ots}")]
    public async Task<IActionResult> Update(string otc, string ots, TransactionType input) =>
        await types.UpdateNameAsync(otc, ots, input.Name) == WriteResult.NotFound ? NotFound() : NoContent();

    [EndpointSummary("Bir işlem tipini siler")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpDelete("{otc}/{ots}")]
    public async Task<IActionResult> Delete(string otc, string ots) =>
        await types.DeleteAsync(otc, ots) ? NoContent() : NotFound();
}
