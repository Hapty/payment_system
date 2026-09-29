using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Api.Controllers;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Tests;

public class BankAccountsControllerTests : TestDatabase
{
    private static BankAccount Account(string no = "ACC1", string status = "Active", decimal balance = 100m) =>
        new() { AccountNo = no, AccountStatus = status, Balance = balance };

    [Fact]
    public async Task GetAll_ReturnsAllAccounts()
    {
        await SeedAsync(Account("ACC1"), Account("ACC2"));

        var result = await new BankAccountsController(NewContext()).GetAll();

        Assert.Equal(["ACC1", "ACC2"], result.Select(x => x.AccountNo).Order());
    }

    [Fact]
    public async Task Get_ExistingAccount_ReturnsIt()
    {
        await SeedAsync(Account(balance: 250m));

        var result = await new BankAccountsController(NewContext()).Get("ACC1");

        Assert.Equal(250m, result.Value!.Balance);
    }

    [Fact]
    public async Task Get_MissingAccount_ReturnsNotFound()
    {
        var result = await new BankAccountsController(NewContext()).Get("NOPE");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_NewAccount_ReturnsCreatedAndPersists()
    {
        var result = await new BankAccountsController(NewContext()).Create(Account(balance: 500m));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(BankAccountsController.Get), created.ActionName);
        Assert.Equal(500m, (await NewContext().BankAccounts.FindAsync("ACC1"))!.Balance);
    }

    [Fact]
    public async Task Create_DuplicateAccount_ReturnsConflict()
    {
        await SeedAsync(Account());

        var result = await new BankAccountsController(NewContext()).Create(Account());

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ChangesStatusButNotBalance()
    {
        await SeedAsync(Account(balance: 100m));

        var result = await new BankAccountsController(NewContext())
            .Update("ACC1", Account(status: "Blocked", balance: 999999m));

        Assert.IsType<NoContentResult>(result);
        var saved = (await NewContext().BankAccounts.FindAsync("ACC1"))!;
        Assert.Equal("Blocked", saved.AccountStatus);
        Assert.Equal(100m, saved.Balance);
    }

    [Fact]
    public async Task Update_MissingAccount_ReturnsNotFound()
    {
        var result = await new BankAccountsController(NewContext()).Update("NOPE", Account("NOPE"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ExistingAccount_RemovesIt()
    {
        await SeedAsync(Account());

        var result = await new BankAccountsController(NewContext()).Delete("ACC1");

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await NewContext().BankAccounts.FindAsync("ACC1"));
    }

    [Fact]
    public async Task Delete_MissingAccount_ReturnsNotFound()
    {
        var result = await new BankAccountsController(NewContext()).Delete("NOPE");

        Assert.IsType<NotFoundResult>(result);
    }
}
