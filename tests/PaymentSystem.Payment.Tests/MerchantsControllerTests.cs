using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class MerchantsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private MerchantRepository Repository => new(Db);
    private MerchantsController Controller => new(new MerchantHandler(Repository, NullLogger<MerchantHandler>.Instance));

    private static Merchant Merchant(string id = "TRENDYOL", string name = "TRENDYOL", string mcc = "5399", string status = "Active") =>
        new() { MerchantId = id, Name = name, MerchantType = mcc, Status = status };

    [Fact]
    public async Task GetAll_ReturnsAllMerchants()
    {
        await Repository.CreateAsync(Merchant("TRENDYOL"));
        await Repository.CreateAsync(Merchant("GETIR", "GETIR", "5411"));

        Assert.Equal(["GETIR", "TRENDYOL"], (await Controller.GetAll()).Select(m => m.MerchantId));   // MerchantId'ye göre sıralı
    }

    [Fact]
    public async Task Get_ExistingMerchant_ReturnsIt()
    {
        await Repository.CreateAsync(Merchant());

        var merchant = (await Controller.Get("TRENDYOL")).Value!;
        Assert.Equal(("TRENDYOL", "5399", "Active"), (merchant.Name, merchant.MerchantType, merchant.Status));
    }

    [Fact]
    public async Task Get_MissingMerchant_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get("NOPE")).Result);

    [Fact]
    public async Task Create_NewMerchant_ReturnsCreatedWithCreatedAt()
    {
        var created = Assert.IsType<CreatedAtActionResult>((await Controller.Create(Merchant())).Result);

        Assert.NotEqual(default, ((Merchant)created.Value!).CreatedAt);     // DB'nin verdiği zaman modele yazılır
        Assert.NotNull(await Repository.GetAsync("TRENDYOL"));
    }

    [Fact]
    public async Task Create_TrimsTrailingSpacesOfMerchantId()
    {
        await Controller.Create(Merchant("TRENDYOL       "));     // F42 gibi 15 karaktere doldurulmuş

        Assert.NotNull(await Repository.GetAsync("TRENDYOL"));
    }

    [Fact]
    public async Task Create_DuplicateMerchant_ReturnsConflict()
    {
        await Repository.CreateAsync(Merchant());

        Assert.IsType<ConflictObjectResult>((await Controller.Create(Merchant(name: "Other"))).Result);
    }

    [Fact]
    public async Task Update_ChangesNameTypeAndStatus()
    {
        await Repository.CreateAsync(Merchant());

        Assert.IsType<NoContentResult>(await Controller.Update("TRENDYOL", Merchant(name: "TRENDYOL GO", mcc: "5411", status: "Blocked")));
        var saved = (await Repository.GetAsync("TRENDYOL"))!;
        Assert.Equal(("TRENDYOL GO", "5411", "Blocked"), (saved.Name, saved.MerchantType, saved.Status));
    }

    [Fact]
    public async Task Update_MissingMerchant_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update("NOPE", Merchant("NOPE")));

    [Fact]
    public async Task Delete_ExistingMerchant_RemovesIt()
    {
        await Repository.CreateAsync(Merchant());

        Assert.IsType<NoContentResult>(await Controller.Delete("TRENDYOL"));
        Assert.Null(await Repository.GetAsync("TRENDYOL"));
    }

    [Fact]
    public async Task Delete_MissingMerchant_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete("NOPE"));
}
