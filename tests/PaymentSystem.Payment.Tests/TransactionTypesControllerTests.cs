using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class TransactionTypesControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private TransactionTypeRepository Repository => new(Db);
    private TransactionTypesController Controller => new(Repository);

    private static TransactionType Type(string otc = "10", string ots = "11", string name = "Sale") =>
        new() { Otc = otc, Ots = ots, Name = name };

    [Fact]
    public async Task GetAll_ReturnsAllTypes()
    {
        await Repository.CreateAsync(Type("40", "10", "BalanceInquiry"));
        await Repository.CreateAsync(Type());

        Assert.Equal(["Sale", "BalanceInquiry"], (await Controller.GetAll()).Select(t => t.Name));   // Otc, Ots sıralı
    }

    [Fact]
    public async Task Get_ExistingType_ReturnsIt()
    {
        await Repository.CreateAsync(Type());

        Assert.Equal("Sale", (await Controller.Get("10", "11")).Value!.Name);
    }

    [Fact]
    public async Task Get_MissingType_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get("99", "99")).Result);

    [Fact]
    public async Task Create_NewType_ReturnsCreatedAndPersists()
    {
        Assert.IsType<CreatedAtActionResult>((await Controller.Create(Type())).Result);
        Assert.NotNull(await Repository.GetAsync("10", "11"));
    }

    [Fact]
    public async Task Create_DuplicateType_ReturnsConflict()
    {
        await Repository.CreateAsync(Type());

        Assert.IsType<ConflictObjectResult>((await Controller.Create(Type(name: "Other"))).Result);
    }

    [Fact]
    public async Task Update_ChangesName()
    {
        await Repository.CreateAsync(Type());

        Assert.IsType<NoContentResult>(await Controller.Update("10", "11", Type(name: "Purchase")));
        Assert.Equal("Purchase", (await Repository.GetAsync("10", "11"))!.Name);
    }

    [Fact]
    public async Task Update_MissingType_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update("99", "99", Type("99", "99")));

    [Fact]
    public async Task Delete_ExistingType_RemovesIt()
    {
        await Repository.CreateAsync(Type());

        Assert.IsType<NoContentResult>(await Controller.Delete("10", "11"));
        Assert.Null(await Repository.GetAsync("10", "11"));
    }

    [Fact]
    public async Task Delete_MissingType_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete("99", "99"));
}
