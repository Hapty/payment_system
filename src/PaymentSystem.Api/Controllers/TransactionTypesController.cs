using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentSystem.Api.Data;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Controllers;

[ApiController]
[Route("api/transaction-types")]
public class TransactionTypesController(PaymentDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<TransactionType>> GetAll() =>
        await db.TransactionTypes.AsNoTracking().ToListAsync();

    [HttpGet("{otc}/{ots}")]
    public async Task<ActionResult<TransactionType>> Get(string otc, string ots) =>
        await db.TransactionTypes.FindAsync(otc, ots) is { } type ? type : NotFound();

    [HttpPost]
    public async Task<ActionResult<TransactionType>> Create(TransactionType type)
    {
        if (await db.TransactionTypes.AnyAsync(x => x.Otc == type.Otc && x.Ots == type.Ots))
            return Conflict($"Transaction type {type.Otc}/{type.Ots} already exists.");

        db.TransactionTypes.Add(type);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { otc = type.Otc, ots = type.Ots }, type);
    }

    [HttpPut("{otc}/{ots}")]
    public async Task<IActionResult> Update(string otc, string ots, TransactionType input)
    {
        var type = await db.TransactionTypes.FindAsync(otc, ots);
        if (type is null) return NotFound();

        type.Name = input.Name;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{otc}/{ots}")]
    public async Task<IActionResult> Delete(string otc, string ots)
    {
        var deleted = await db.TransactionTypes.Where(x => x.Otc == otc && x.Ots == ots).ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : NoContent();
    }
}
