using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/merchants")]
public class MerchantsController(MerchantHandler merchants) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<Merchant>> GetAll() => merchants.GetAllAsync();

    [HttpGet("{merchantId}")]
    public async Task<ActionResult<Merchant>> Get(string merchantId) =>
        await merchants.GetAsync(merchantId) is { } merchant ? merchant : NotFound();

    [HttpPost]
    public async Task<ActionResult<Merchant>> Create(Merchant merchant) =>
        await merchants.CreateAsync(merchant) == WriteResult.Duplicate
            ? Conflict($"Merchant {merchant.MerchantId} already exists.")
            : CreatedAtAction(nameof(Get), new { merchantId = merchant.MerchantId }, merchant);

    // Anahtar URL'den gelir; body'den Name, MerchantType ve Status kullanılır.
    [HttpPut("{merchantId}")]
    public async Task<IActionResult> Update(string merchantId, Merchant input) =>
        await merchants.UpdateAsync(merchantId, input.Name, input.MerchantType, input.Status) == WriteResult.NotFound
            ? NotFound()
            : NoContent();

    [HttpDelete("{merchantId}")]
    public async Task<IActionResult> Delete(string merchantId) =>
        await merchants.DeleteAsync(merchantId) ? NoContent() : NotFound();
}
