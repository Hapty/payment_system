using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class BankAccountsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private BankAccountRepository Repository => new(Db);
    private BankAccountsController Controller => new(new BankAccountHandler(Repository, NullLogger<BankAccountHandler>.Instance));

    private static BankAccount Account(long no = 1000000001, string status = "Active", decimal balance = 100m, string? email = null) =>
        new() { AccountNo = no, AccountStatus = status, Balance = balance, Email = email };

    [Fact]
    public async Task GetAll_ReturnsAllAccounts()
    {
        await Repository.CreateAsync(Account(1000000002));
        await Repository.CreateAsync(Account(1000000001));

        var result = await Controller.GetAll();

        Assert.Equal([1000000001L, 1000000002L], result.Select(x => x.AccountNo));   // AccountNo'ya göre sıralı
    }

    [Fact]
    public async Task Get_ExistingAccount_ReturnsIt()
    {
        await Repository.CreateAsync(Account(balance: 250.75m));

        var result = await Controller.Get(1000000001);

        Assert.Equal(250.75m, result.Value!.Balance);
    }

    [Fact]
    public async Task Get_MissingAccount_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get(9999999999)).Result);

    [Fact]
    public async Task Create_NewAccount_ReturnsCreatedAndPersists()
    {
        var result = await Controller.Create(Account(balance: 500m));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(BankAccountsController.Get), created.ActionName);
        Assert.Equal(500m, (await Repository.GetAsync(1000000001))!.Balance);
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

        var result = await Controller.Update(1000000001, Account(status: "Blocked", balance: 999999m));

        Assert.IsType<NoContentResult>(result);
        var saved = (await Repository.GetAsync(1000000001))!;
        Assert.Equal("Blocked", saved.AccountStatus);
        Assert.Equal(100m, saved.Balance);
    }

    [Fact]
    public async Task Create_WithEmail_PersistsIt()
    {
        await Controller.Create(Account(email: "musteri@example.com"));

        Assert.Equal("musteri@example.com", (await Repository.GetAsync(1000000001))!.Email);
    }

    [Fact]
    public async Task Update_SetsAndClearsEmail()
    {
        await Repository.CreateAsync(Account());

        await Controller.Update(1000000001, Account(email: "yeni@example.com"));
        Assert.Equal("yeni@example.com", (await Repository.GetAsync(1000000001))!.Email);

        await Controller.Update(1000000001, Account());
        Assert.Null((await Repository.GetAsync(1000000001))!.Email);
    }

    [Fact]
    public async Task Update_MissingAccount_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update(9999999999, Account(9999999999)));

    [Fact]
    public async Task Delete_ExistingAccount_RemovesIt()
    {
        await Repository.CreateAsync(Account());

        Assert.IsType<NoContentResult>(await Controller.Delete(1000000001));
        Assert.Null(await Repository.GetAsync(1000000001));
    }

    [Fact]
    public async Task Delete_MissingAccount_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete(9999999999));
}
