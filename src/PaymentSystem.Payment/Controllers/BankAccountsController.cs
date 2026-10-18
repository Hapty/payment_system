using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/bank-accounts")]
public class BankAccountsController(BankAccountHandler accounts) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BankAccount>> GetAll() => accounts.GetAllAsync();

    [HttpGet("{accountNo:long}")]
    public async Task<ActionResult<BankAccount>> Get(long accountNo) =>
        await accounts.GetAsync(accountNo) is { } account ? account : NotFound();

    [HttpPost]
    public async Task<ActionResult<BankAccount>> Create(BankAccount account) =>
        await accounts.CreateAsync(account) == WriteResult.Duplicate
            ? Conflict($"Account {account.AccountNo} already exists.")
            : CreatedAtAction(nameof(Get), new { accountNo = account.AccountNo }, account);

    // Body'deki Balance yok sayılır: bakiye sadece hesap açılırken verilir, sonra işlemlerle değişir.
    // Email body'de yoksa silinir (PUT tüm düzenlenebilir alanları yazar).
    [HttpPut("{accountNo:long}")]
    public async Task<IActionResult> Update(long accountNo, BankAccount input) =>
        await accounts.UpdateAsync(accountNo, input.AccountStatus, input.Email) == WriteResult.NotFound ? NotFound() : NoContent();

    [HttpDelete("{accountNo:long}")]
    public async Task<IActionResult> Delete(long accountNo) =>
        await accounts.DeleteAsync(accountNo) ? NoContent() : NotFound();
}
