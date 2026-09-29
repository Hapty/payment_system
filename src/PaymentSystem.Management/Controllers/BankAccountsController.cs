using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentSystem.Management.Data;
using PaymentSystem.Management.Models;

namespace PaymentSystem.Management.Controllers;

[ApiController]
[Route("api/bank-accounts")]
public class BankAccountsController(ManagementDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<BankAccount>> GetAll() =>
        await db.BankAccounts.AsNoTracking().ToListAsync();

    [HttpGet("{accountNo}")]
    public async Task<ActionResult<BankAccount>> Get(string accountNo) =>
        await db.BankAccounts.FindAsync(accountNo) is { } account ? account : NotFound();

    [HttpPost]
    public async Task<ActionResult<BankAccount>> Create(BankAccount account)
    {
        if (await db.BankAccounts.AnyAsync(x => x.AccountNo == account.AccountNo))
            return Conflict($"Account {account.AccountNo} already exists.");

        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { accountNo = account.AccountNo }, account);
    }

    // Balance is only set on create; afterwards it changes through transactions (Transaction service), not here.
    [HttpPut("{accountNo}")]
    public async Task<IActionResult> Update(string accountNo, BankAccount input)
    {
        var account = await db.BankAccounts.FindAsync(accountNo);
        if (account is null) return NotFound();

        account.AccountStatus = input.AccountStatus;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{accountNo}")]
    public async Task<IActionResult> Delete(string accountNo)
    {
        var deleted = await db.BankAccounts.Where(x => x.AccountNo == accountNo).ExecuteDeleteAsync();
        return deleted == 0 ? NotFound() : NoContent();
    }
}
