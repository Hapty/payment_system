using Dapper;
using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class CardsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private CardRepository Repository => new(Db);
    private CardsController Controller => new(Repository);

    private static Card NewCard(string number = "4111111111111111") => new()
    {
        CardNumber = number,
        AccountNo = "ACC1",
        CardType = "Debit",
        ExpiryDate = "1228",
        ContactlessAllowed = true,
        ContactlessLimit = 750m
    };

    private async Task<int> SeedCardAsync(string number = "4111111111111111")
    {
        var card = NewCard(number);
        await Repository.CreateAsync(card);
        return card.Id;
    }

    [Fact]
    public async Task GetAll_ReturnsAllCards()
    {
        await SeedCardAsync("1111");
        await SeedCardAsync("2222");

        Assert.Equal(["1111", "2222"], (await Controller.GetAll()).Select(c => c.CardNumber));
    }

    [Fact]
    public async Task Get_ExistingCard_ReturnsAllColumns()
    {
        var id = await SeedCardAsync();

        var card = (await Controller.Get(id)).Value!;

        Assert.Equal(("4111111111111111", "ACC1", "Debit", "1228"), (card.CardNumber, card.AccountNo, card.CardType, card.ExpiryDate));
        Assert.True(card.ContactlessAllowed);
        Assert.Equal(750m, card.ContactlessLimit);
    }

    [Fact]
    public async Task Get_MissingCard_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.Get(999_999)).Result);

    [Fact]
    public async Task GetByNumber_ExistingCard_ReturnsIt()
    {
        var id = await SeedCardAsync();

        Assert.Equal(id, (await Controller.GetByNumber("4111111111111111")).Value!.Id);
    }

    [Fact]
    public async Task GetByNumber_MissingCard_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>((await Controller.GetByNumber("0000")).Result);

    [Fact]
    public async Task Create_NewCard_GetsIdFromDatabaseAndIgnoresTransactionFields()
    {
        var card = NewCard();
        card.Id = 42;
        card.LastTransactionDate = DateTime.UtcNow;
        card.LastTransactionAmount = 99m;

        var result = await Controller.Create(card);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.IsType<Card>(created.Value);
        Assert.NotEqual(42, saved.Id);
        var stored = (await Repository.GetAsync(saved.Id))!;
        Assert.Null(stored.LastTransactionDate);
        Assert.Null(stored.LastTransactionAmount);
    }

    [Fact]
    public async Task Create_DuplicateCardNumber_ReturnsConflict()
    {
        await SeedCardAsync();

        Assert.IsType<ConflictObjectResult>((await Controller.Create(NewCard())).Result);
    }

    [Fact]
    public async Task Update_ChangesEditableFieldsButNotTransactionFields()
    {
        var id = await SeedCardAsync();
        // Son işlem bilgisini işlem akışı yazmış gibi doğrudan SQL ile koy.
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("UPDATE dbo.Card SET LastTransactionAmount = 10.50 WHERE Id = @id", new { id });

        var input = NewCard("5555444433332222");
        input.CardType = "Credit";
        input.ContactlessAllowed = false;
        input.ContactlessLimit = null;
        input.LastTransactionAmount = 123m;

        Assert.IsType<NoContentResult>(await Controller.Update(id, input));

        var saved = (await Repository.GetAsync(id))!;
        Assert.Equal(("5555444433332222", "Credit"), (saved.CardNumber, saved.CardType));
        Assert.False(saved.ContactlessAllowed);
        Assert.Null(saved.ContactlessLimit);
        Assert.Equal(10.50m, saved.LastTransactionAmount);   // CRUD ile değişmedi
    }

    [Fact]
    public async Task Update_ToAnotherCardsNumber_ReturnsConflict()
    {
        await SeedCardAsync("1111");
        var id = await SeedCardAsync("2222");

        Assert.IsType<ConflictObjectResult>(await Controller.Update(id, NewCard("1111")));
    }

    [Fact]
    public async Task Update_KeepingOwnNumber_Succeeds()
    {
        var id = await SeedCardAsync("1111");

        Assert.IsType<NoContentResult>(await Controller.Update(id, NewCard("1111")));
    }

    [Fact]
    public async Task Update_MissingCard_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Update(999_999, NewCard()));

    [Fact]
    public async Task Delete_ExistingCard_RemovesIt()
    {
        var id = await SeedCardAsync();

        Assert.IsType<NoContentResult>(await Controller.Delete(id));
        Assert.Null(await Repository.GetAsync(id));
    }

    [Fact]
    public async Task Delete_MissingCard_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(await Controller.Delete(999_999));
}
