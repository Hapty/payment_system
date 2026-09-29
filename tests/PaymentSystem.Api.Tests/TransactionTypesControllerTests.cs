using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Api.Controllers;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Tests;

public class TransactionTypesControllerTests : TestDatabase
{
    private static TransactionType Type(string otc = "10", string ots = "11", string name = "Sale") =>
        new() { Otc = otc, Ots = ots, Name = name };

    [Fact]
    public async Task GetAll_ReturnsAllTypes()
    {
        await SeedAsync(Type("10", "11"), Type("40", "10", "BalanceInquiry"));

        var result = await new TransactionTypesController(NewContext()).GetAll();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Get_ExistingType_ReturnsIt()
    {
        await SeedAsync(Type());

        var result = await new TransactionTypesController(NewContext()).Get("10", "11");

        Assert.Equal("Sale", result.Value!.Name);
    }

    [Fact]
    public async Task Get_MissingType_ReturnsNotFound()
    {
        var result = await new TransactionTypesController(NewContext()).Get("99", "99");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_NewType_ReturnsCreatedAndPersists()
    {
        var result = await new TransactionTypesController(NewContext()).Create(Type());

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(await NewContext().TransactionTypes.FindAsync("10", "11"));
    }

    [Fact]
    public async Task Create_DuplicateType_ReturnsConflict()
    {
        await SeedAsync(Type());

        var result = await new TransactionTypesController(NewContext()).Create(Type(name: "Other"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ChangesName()
    {
        await SeedAsync(Type());

        var result = await new TransactionTypesController(NewContext()).Update("10", "11", Type(name: "Purchase"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Purchase", (await NewContext().TransactionTypes.FindAsync("10", "11"))!.Name);
    }

    [Fact]
    public async Task Update_MissingType_ReturnsNotFound()
    {
        var result = await new TransactionTypesController(NewContext()).Update("99", "99", Type("99", "99"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ExistingType_RemovesIt()
    {
        await SeedAsync(Type());

        var result = await new TransactionTypesController(NewContext()).Delete("10", "11");

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await NewContext().TransactionTypes.FindAsync("10", "11"));
    }

    [Fact]
    public async Task Delete_MissingType_ReturnsNotFound()
    {
        var result = await new TransactionTypesController(NewContext()).Delete("99", "99");

        Assert.IsType<NotFoundResult>(result);
    }
}
