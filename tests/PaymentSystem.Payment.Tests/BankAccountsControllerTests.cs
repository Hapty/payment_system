using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class BankAccountsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private BankAccountRepository Repository => new(Db);
    private BankAccountsController Controller => new(Repository);

    private static BankAccount Account(string no = "ACC1", string status = "Active", decimal balance = 100m) =>
        new() { AccountNo = no, AccountStatus = status, Balance = balance };

    [Fact]
    public async Task GetAll_ReturnsAllAccounts()
    {
        await Repository.CreateAsync(Account("ACC2"));
        await Repository.CreateAsync(Account("ACC1"));

        var result = await Controller.GetAll();

        Assert.Equal(["ACC1", "ACC2"], result.Select(x => x.AccountNo));   // AccountNo'ya göre sıralı
    }

    [Fact]
    public async Task Get_ExistingAccount_ReturnsIt()
    {
        await Repository.CreateAsync(Account(balance: 250.75m));

        var result = await Controller.Get("ACC1");

        Assert.Equal(250.75m, result.Value!.Balance);
    }

    [Fact]
    public async Task Get_MissingAccount_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get("NOPE")).Result);

    [Fact]
    public async Task Create_NewAccount_ReturnsCreatedAndPersists()
    {
        var result = await Controller.Create(Account(balance: 500m));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(BankAccountsController.Get), created.ActionName);
        Assert.Equal(500m, (await Repository.GetAsync("ACC1"))!.Balance);
    }

    [Fact]
    public async Task Create_DuplicateAccount_ReturnsConflict()
    {
        await Repository.CreateAsync(Account());

        Assert.IsType<ConflictObjectResult>((await Controller.Create(Account())).Result);
    }

    [Fact]
    public async Task Update_ChangesStatusButNotBalance()
    {
        await Repository.CreateAsync(Account(balance: 100m));

        var result = await Controller.Update("ACC1", Account(status: "Blocked", balance: 999999m));

        Assert.IsType<NoContentResult>(result);
        var saved = (await Repository.GetAsync("ACC1"))!;
        Assert.Equal("Blocked", saved.AccountStatus);
        Assert.Equal(100m, saved.Balance);
    }

    [Fact]
    public async Task Update_MissingAccount_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update("NOPE", Account("NOPE")));

    [Fact]
    public async Task Delete_ExistingAccount_RemovesIt()
    {
        await Repository.CreateAsync(Account());

        Assert.IsType<NoContentResult>(await Controller.Delete("ACC1"));
        Assert.Null(await Repository.GetAsync("ACC1"));
    }

    [Fact]
    public async Task Delete_MissingAccount_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete("NOPE"));
}
