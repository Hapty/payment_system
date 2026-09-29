using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Api.Controllers;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Tests;

public class MtiProcessingCodesControllerTests : TestDatabase
{
    private static MtiProcessingCode Mapping(string mti = "0200", string f3 = "000000", string otc = "10", string ots = "11") =>
        new() { Mti = mti, F3_ProcessingCode = f3, Otc = otc, Ots = ots };

    [Fact]
    public async Task GetAll_ReturnsAllMappings()
    {
        await SeedAsync(Mapping(), Mapping(f3: "310000", otc: "40", ots: "10"));

        var result = await new MtiProcessingCodesController(NewContext()).GetAll();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Get_ExistingMapping_ReturnsIt()
    {
        await SeedAsync(Mapping());

        var result = await new MtiProcessingCodesController(NewContext()).Get("0200", "000000");

        Assert.Equal("10", result.Value!.Otc);
    }

    [Fact]
    public async Task Get_MissingMapping_ReturnsNotFound()
    {
        var result = await new MtiProcessingCodesController(NewContext()).Get("9999", "999999");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_NewMapping_ReturnsCreatedAndPersists()
    {
        var result = await new MtiProcessingCodesController(NewContext()).Create(Mapping());

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(await NewContext().MtiProcessingCodes.FindAsync("0200", "000000"));
    }

    [Fact]
    public async Task Create_DuplicateMapping_ReturnsConflict()
    {
        await SeedAsync(Mapping());

        var result = await new MtiProcessingCodesController(NewContext()).Create(Mapping(otc: "11"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ChangesOtcOts()
    {
        await SeedAsync(Mapping());

        var result = await new MtiProcessingCodesController(NewContext())
            .Update("0200", "000000", Mapping(otc: "40", ots: "10"));

        Assert.IsType<NoContentResult>(result);
        var saved = (await NewContext().MtiProcessingCodes.FindAsync("0200", "000000"))!;
        Assert.Equal(("40", "10"), (saved.Otc, saved.Ots));
    }

    [Fact]
    public async Task Update_MissingMapping_ReturnsNotFound()
    {
        var result = await new MtiProcessingCodesController(NewContext()).Update("9999", "999999", Mapping());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ExistingMapping_RemovesIt()
    {
        await SeedAsync(Mapping());

        var result = await new MtiProcessingCodesController(NewContext()).Delete("0200", "000000");

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await NewContext().MtiProcessingCodes.FindAsync("0200", "000000"));
    }

    [Fact]
    public async Task Delete_MissingMapping_ReturnsNotFound()
    {
        var result = await new MtiProcessingCodesController(NewContext()).Delete("9999", "999999");

        Assert.IsType<NotFoundResult>(result);
    }
}
