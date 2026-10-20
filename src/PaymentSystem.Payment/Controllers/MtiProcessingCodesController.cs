using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/mti-processing-codes")]
public class MtiProcessingCodesController(MtiProcessingCodeHandler codes) : ControllerBase
{
    [EndpointSummary("Tüm MTI + işlem kodu eşlemelerini listeler")]
    [HttpGet]
    public Task<IReadOnlyList<MtiProcessingCode>> GetAll() => codes.GetAllAsync();

    [EndpointSummary("Bir MTI + işlem kodu eşlemesini MTI ve işlem kodu ile getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{mti}/{processingCode}")]
    public async Task<ActionResult<MtiProcessingCode>> Get(string mti, string processingCode) =>
        await codes.GetAsync(mti, processingCode) is { } code ? code : NotFound();

    [EndpointSummary("Yeni MTI + işlem kodu eşlemesi oluşturur")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<string>(StatusCodes.Status409Conflict, "text/plain")]
    [HttpPost]
    public async Task<ActionResult<MtiProcessingCode>> Create(MtiProcessingCode code) =>
        await codes.CreateAsync(code) == WriteResult.Duplicate
            ? Conflict($"Mapping {code.Mti}/{code.F3_ProcessingCode} already exists.")
            : CreatedAtAction(nameof(Get), new { mti = code.Mti, processingCode = code.F3_ProcessingCode }, code);

    // Anahtar URL'den gelir; body'den sadece Otc ve Ots kullanılır.
    [EndpointSummary("Bir MTI + işlem kodu eşlemesini günceller")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpPut("{mti}/{processingCode}")]
    public async Task<IActionResult> Update(string mti, string processingCode, MtiProcessingCode input) =>
        await codes.UpdateTargetAsync(mti, processingCode, input.Otc, input.Ots) == WriteResult.NotFound ? NotFound() : NoContent();

    [EndpointSummary("Bir MTI + işlem kodu eşlemesini siler")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpDelete("{mti}/{processingCode}")]
    public async Task<IActionResult> Delete(string mti, string processingCode) =>
        await codes.DeleteAsync(mti, processingCode) ? NoContent() : NotFound();
}
