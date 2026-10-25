using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/merchants")]
public class MerchantsController(MerchantHandler merchants) : ControllerBase
{
    [EndpointSummary("Tüm işyerlerini listeler")]
    [HttpGet]
    public Task<IReadOnlyList<Merchant>> GetAll() => merchants.GetAllAsync();

    [EndpointSummary("Bir işyerini işyeri no ile getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{merchantId}")]
    public async Task<ActionResult<Merchant>> Get(string merchantId) =>
        await merchants.GetAsync(merchantId) is { } merchant ? merchant : NotFound();

    [EndpointSummary("Yeni işyeri oluşturur")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<string>(StatusCodes.Status409Conflict, "text/plain")]
    [HttpPost]
    public async Task<ActionResult<Merchant>> Create(Merchant merchant) =>
        await merchants.CreateAsync(merchant) == WriteResult.Duplicate
            ? Conflict($"Merchant {merchant.MerchantId} already exists.")
            : CreatedAtAction(nameof(Get), new { merchantId = merchant.MerchantId }, merchant);

    // Anahtar URL'den gelir; body'den Name, MerchantType ve Status kullanılır.
    [EndpointSummary("Bir işyerini günceller")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpPut("{merchantId}")]
    public async Task<IActionResult> Update(string merchantId, Merchant input) =>
        await merchants.UpdateAsync(merchantId, input.Name, input.MerchantType, input.Status) == WriteResult.NotFound
            ? NotFound()
            : NoContent();

    [EndpointSummary("Bir işyerini siler")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpDelete("{merchantId}")]
    public async Task<IActionResult> Delete(string merchantId) =>
        await merchants.DeleteAsync(merchantId) ? NoContent() : NotFound();
}
