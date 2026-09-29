using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentSystem.Management.Data;
using PaymentSystem.Management.Models;

namespace PaymentSystem.Management.Controllers;

[ApiController]
[Route("api/mti-processing-codes")]
public class MtiProcessingCodesController(ManagementDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<MtiProcessingCode>> GetAll() =>
        await db.MtiProcessingCodes.AsNoTracking().ToListAsync();

    [HttpGet("{mti}/{processingCode}")]
    public async Task<ActionResult<MtiProcessingCode>> Get(string mti, string processingCode) =>
        await db.MtiProcessingCodes.FindAsync(mti, processingCode) is { } code ? code : NotFound();

    [HttpPost]
    public async Task<ActionResult<MtiProcessingCode>> Create(MtiProcessingCode code)
    {
        if (await db.MtiProcessingCodes.AnyAsync(x => x.Mti == code.Mti && x.F3_ProcessingCode == code.F3_ProcessingCode))
            return Conflict($"Mapping {code.Mti}/{code.F3_ProcessingCode} already exists.");

        db.MtiProcessingCodes.Add(code);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { mti = code.Mti, processingCode = code.F3_ProcessingCode }, code);
    }

    [HttpPut("{mti}/{processingCode}")]
    public async Task<IActionResult> Update(string mti, string processingCode, MtiProcessingCode input)
    {
        var code = await db.MtiProcessingCodes.FindAsync(mti, processingCode);
        if (code is null) return NotFound();

        code.Otc = input.Otc;
        code.Ots = input.Ots;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{mti}/{processingCode}")]
    public async Task<IActionResult> Delete(string mti, string processingCode)
    {
        var deleted = await db.MtiProcessingCodes
            .Where(x => x.Mti == mti && x.F3_ProcessingCode == processingCode)
            .ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : NoContent();
    }
}
