using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class CreditLimitsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private const string CreditPan = "4111111111111111";
    private const string DebitPan = "5555444433332222";

    private CreditCardLimitRepository Repository => new(Db);
    private CreditLimitsController Controller =>
        new(new CreditCardLimitHandler(Repository, new CardRepository(Db), NullLogger<CreditCardLimitHandler>.Instance));

    private static CreditCardLimit Limit(string cardNumber = CreditPan, decimal creditLimit = 50_000m, decimal cashLimit = 10_000m) =>
        new() { CardNumber = cardNumber, CreditLimit = creditLimit, CashLimit = cashLimit, StatementDay = 25, DueDay = 5 };

    private async Task SeedCardsAsync()
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.Card (CardNumber, AccountNo, CardType, ExpiryDate, ContactlessAllowed)
            VALUES (@CreditPan, 1000000001, 'Credit', '1228', 0), (@DebitPan, 1000000001, 'Debit', '1228', 0)
            """,
            new { CreditPan, DebitPan });
    }

    [Fact]
    public async Task Create_ForCreditCard_ReturnsCreatedWithNothingUsed()
    {
        await SeedCardsAsync();
        var input = Limit();
        input.UsedLimit = 999m;     // body'deki kullanılan tutar yok sayılır

        var created = Assert.IsType<CreatedAtActionResult>((await Controller.Create(input)).Result);

        var limit = (CreditCardLimit)created.Value!;
        Assert.Equal((0m, 50_000m, 0m), (limit.UsedLimit, limit.AvailableLimit, limit.UsedCashLimit));
        var saved = (await Repository.GetAsync(CreditPan))!;
        Assert.Equal((50_000m, 0m, 10_000m, (byte)25, (byte)5), (saved.CreditLimit, saved.UsedLimit, saved.CashLimit, saved.StatementDay, saved.DueDay));
    }

    [Fact]
    public async Task Create_ForDebitCard_ReturnsBadRequest()
    {
        await SeedCardsAsync();

        Assert.IsType<BadRequestObjectResult>((await Controller.Create(Limit(DebitPan))).Result);
        Assert.Null(await Repository.GetAsync(DebitPan));
    }

    [Fact]
    public async Task Create_ForMissingCard_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Create(Limit("1111"))).Result);

    [Fact]
    public async Task Create_Duplicate_ReturnsConflict()
    {
        await SeedCardsAsync();
        await Controller.Create(Limit());

        Assert.IsType<ConflictObjectResult>((await Controller.Create(Limit(creditLimit: 1m, cashLimit: 0m))).Result);
    }

    [Fact]
    public async Task GetAll_And_Get_ReturnLimitsWithAvailableLimit()
    {
        await SeedCardsAsync();
        await Controller.Create(Limit());
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("UPDATE dbo.CreditCardLimit SET UsedLimit = 1500 WHERE CardNumber = @CreditPan", new { CreditPan });

        Assert.Equal([CreditPan], (await Controller.GetAll()).Select(l => l.CardNumber));
        var limit = (await Controller.Get(CreditPan)).Value!;
        Assert.Equal((1500m, 48_500m), (limit.UsedLimit, limit.AvailableLimit));
    }

    [Fact]
    public async Task Get_Missing_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get("1111")).Result);

    [Fact]
    public async Task Update_ChangesLimitsAndDaysButNotUsedAmounts()
    {
        await SeedCardsAsync();
        await Controller.Create(Limit());
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("UPDATE dbo.CreditCardLimit SET UsedLimit = 1500, UsedCashLimit = 500 WHERE CardNumber = @CreditPan", new { CreditPan });
        var input = Limit(creditLimit: 60_000m, cashLimit: 5_000m);
        input.StatementDay = 10;
        input.DueDay = 20;
        input.UsedLimit = 0m;

        Assert.IsType<NoContentResult>(await Controller.Update(CreditPan, input));

        var saved = (await Repository.GetAsync(CreditPan))!;
        Assert.Equal((60_000m, 5_000m, (byte)10, (byte)20), (saved.CreditLimit, saved.CashLimit, saved.StatementDay, saved.DueDay));
        Assert.Equal((1500m, 500m, 58_500m), (saved.UsedLimit, saved.UsedCashLimit, saved.AvailableLimit));
    }

    [Fact]
    public async Task Update_Missing_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update("1111", Limit("1111")));

    [Fact]
    public async Task Delete_RemovesLimit()
    {
        await SeedCardsAsync();
        await Controller.Create(Limit());

        Assert.IsType<NoContentResult>(await Controller.Delete(CreditPan));
        Assert.Null(await Repository.GetAsync(CreditPan));
    }

    [Fact]
    public async Task Delete_Missing_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete("1111"));

    [Fact]
    public async Task DeletingCard_DeletesItsLimit()
    {
        await SeedCardsAsync();
        await Controller.Create(Limit());

        await using (var connection = Db.Create())
            await connection.ExecuteAsync("DELETE FROM dbo.Card WHERE CardNumber = @CreditPan", new { CreditPan });

        Assert.Null(await Repository.GetAsync(CreditPan));
    }
}
