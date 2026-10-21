using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/bank-accounts")]
public class BankAccountsController(BankAccountHandler accounts) : ControllerBase
{
    [EndpointSummary("Tüm banka hesaplarını listeler")]
    [HttpGet]
    public Task<IReadOnlyList<BankAccount>> GetAll() => accounts.GetAllAsync();

    [EndpointSummary("Bir banka hesabını hesap no ile getirir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("{accountNo:long}")]
    public async Task<ActionResult<BankAccount>> Get(long accountNo) =>
        await accounts.GetAsync(accountNo) is { } account ? account : NotFound();

    [EndpointSummary("Yeni banka hesabı oluşturur")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<string>(StatusCodes.Status409Conflict, "text/plain")]
    [HttpPost]
    public async Task<ActionResult<BankAccount>> Create(BankAccount account) =>
        await accounts.CreateAsync(account) == WriteResult.Duplicate
            ? Conflict($"Account {account.AccountNo} already exists.")
            : CreatedAtAction(nameof(Get), new { accountNo = account.AccountNo }, account);

    // Body'deki Balance yok sayılır: bakiye sadece hesap açılırken verilir, sonra işlemlerle değişir.
    // Email zorunludur (boş kolonlu hesap olmaz); Balance body'de olmalı ama yok sayılır.
    [EndpointSummary("Bir banka hesabını günceller")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpPut("{accountNo:long}")]
    public async Task<IActionResult> Update(long accountNo, BankAccount input) =>
        await accounts.UpdateAsync(accountNo, input.AccountStatus, input.Email) == WriteResult.NotFound ? NotFound() : NoContent();

    [EndpointSummary("Bir banka hesabını siler")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpDelete("{accountNo:long}")]
    public async Task<IActionResult> Delete(long accountNo) =>
        await accounts.DeleteAsync(accountNo) ? NoContent() : NotFound();
}
