using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class MtiProcessingCodesControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private MtiProcessingCodeRepository Repository => new(Db);
    private MtiProcessingCodesController Controller => new(Repository);

    private static MtiProcessingCode Mapping(string mti = "0200", string f3 = "000000", string otc = "10", string ots = "11") =>
        new() { Mti = mti, F3_ProcessingCode = f3, Otc = otc, Ots = ots };

    [Fact]
    public async Task GetAll_ReturnsAllMappings()
    {
        await Repository.CreateAsync(Mapping(f3: "310000", otc: "40", ots: "10"));
        await Repository.CreateAsync(Mapping());

        Assert.Equal(["000000", "310000"], (await Controller.GetAll()).Select(m => m.F3_ProcessingCode));
    }

    [Fact]
    public async Task Get_ExistingMapping_ReturnsIt()
    {
        await Repository.CreateAsync(Mapping());

        var mapping = (await Controller.Get("0200", "000000")).Value!;

        Assert.Equal(("10", "11"), (mapping.Otc, mapping.Ots));
    }

    [Fact]
    public async Task Get_MissingMapping_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get("9999", "999999")).Result);

    [Fact]
    public async Task Create_NewMapping_ReturnsCreatedAndPersists()
    {
        Assert.IsType<CreatedAtActionResult>((await Controller.Create(Mapping())).Result);
        Assert.NotNull(await Repository.GetAsync("0200", "000000"));
    }

    [Fact]
    public async Task Create_DuplicateMapping_ReturnsConflict()
    {
        await Repository.CreateAsync(Mapping());

        Assert.IsType<ConflictObjectResult>((await Controller.Create(Mapping(otc: "11"))).Result);
    }

    [Fact]
    public async Task Update_ChangesOtcOts()
    {
        await Repository.CreateAsync(Mapping());

        Assert.IsType<NoContentResult>(await Controller.Update("0200", "000000", Mapping(otc: "40", ots: "10")));
        var saved = (await Repository.GetAsync("0200", "000000"))!;
        Assert.Equal(("40", "10"), (saved.Otc, saved.Ots));
    }

    [Fact]
    public async Task Update_MissingMapping_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update("9999", "999999", Mapping()));

    [Fact]
    public async Task Delete_ExistingMapping_RemovesIt()
    {
        await Repository.CreateAsync(Mapping());

        Assert.IsType<NoContentResult>(await Controller.Delete("0200", "000000"));
        Assert.Null(await Repository.GetAsync("0200", "000000"));
    }

    [Fact]
    public async Task Delete_MissingMapping_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete("9999", "999999"));
}
