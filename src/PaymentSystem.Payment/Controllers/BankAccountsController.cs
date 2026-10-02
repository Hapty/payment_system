using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Controllers;

[ApiController]
[Route("api/bank-accounts")]
public class BankAccountsController(BankAccountRepository accounts) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BankAccount>> GetAll() => accounts.GetAllAsync();

    [HttpGet("{accountNo}")]
    public async Task<ActionResult<BankAccount>> Get(string accountNo) =>
        await accounts.GetAsync(accountNo) is { } account ? account : NotFound();

    [HttpPost]
    public async Task<ActionResult<BankAccount>> Create(BankAccount account) =>
        await accounts.CreateAsync(account) == WriteResult.Duplicate
            ? Conflict($"Account {account.AccountNo} already exists.")
            : CreatedAtAction(nameof(Get), new { accountNo = account.AccountNo }, account);

    // Body'deki Balance yok sayılır: bakiye sadece hesap açılırken verilir, sonra işlemlerle değişir.
    [HttpPut("{accountNo}")]
    public async Task<IActionResult> Update(string accountNo, BankAccount input) =>
        await accounts.UpdateStatusAsync(accountNo, input.AccountStatus) == WriteResult.NotFound ? NotFound() : NoContent();

    [HttpDelete("{accountNo}")]
    public async Task<IActionResult> Delete(string accountNo) =>
        await accounts.DeleteAsync(accountNo) ? NoContent() : NotFound();
}
