using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/transaction-types")]
public class TransactionTypesController(TransactionTypeRepository types) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<TransactionType>> GetAll() => types.GetAllAsync();

    [HttpGet("{otc}/{ots}")]
    public async Task<ActionResult<TransactionType>> Get(string otc, string ots) =>
        await types.GetAsync(otc, ots) is { } type ? type : NotFound();

    [HttpPost]
    public async Task<ActionResult<TransactionType>> Create(TransactionType type) =>
        await types.CreateAsync(type) == WriteResult.Duplicate
            ? Conflict($"Transaction type {type.Otc}/{type.Ots} already exists.")
            : CreatedAtAction(nameof(Get), new { otc = type.Otc, ots = type.Ots }, type);

    // Anahtar URL'den gelir; body'den sadece Name kullanılır.
    [HttpPut("{otc}/{ots}")]
    public async Task<IActionResult> Update(string otc, string ots, TransactionType input) =>
        await types.UpdateNameAsync(otc, ots, input.Name) == WriteResult.NotFound ? NotFound() : NoContent();

    [HttpDelete("{otc}/{ots}")]
    public async Task<IActionResult> Delete(string otc, string ots) =>
        await types.DeleteAsync(otc, ots) ? NoContent() : NotFound();
}
