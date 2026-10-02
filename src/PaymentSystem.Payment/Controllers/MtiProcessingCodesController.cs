using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/mti-processing-codes")]
public class MtiProcessingCodesController(MtiProcessingCodeRepository codes) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<MtiProcessingCode>> GetAll() => codes.GetAllAsync();

    [HttpGet("{mti}/{processingCode}")]
    public async Task<ActionResult<MtiProcessingCode>> Get(string mti, string processingCode) =>
        await codes.GetAsync(mti, processingCode) is { } code ? code : NotFound();

    [HttpPost]
    public async Task<ActionResult<MtiProcessingCode>> Create(MtiProcessingCode code) =>
        await codes.CreateAsync(code) == WriteResult.Duplicate
            ? Conflict($"Mapping {code.Mti}/{code.F3_ProcessingCode} already exists.")
            : CreatedAtAction(nameof(Get), new { mti = code.Mti, processingCode = code.F3_ProcessingCode }, code);

    // Anahtar URL'den gelir; body'den sadece Otc ve Ots kullanılır.
    [HttpPut("{mti}/{processingCode}")]
    public async Task<IActionResult> Update(string mti, string processingCode, MtiProcessingCode input) =>
        await codes.UpdateTargetAsync(mti, processingCode, input.Otc, input.Ots) == WriteResult.NotFound ? NotFound() : NoContent();

    [HttpDelete("{mti}/{processingCode}")]
    public async Task<IActionResult> Delete(string mti, string processingCode) =>
        await codes.DeleteAsync(mti, processingCode) ? NoContent() : NotFound();
}
