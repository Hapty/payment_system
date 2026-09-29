using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Api.Controllers;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Tests;

public class CardsControllerTests : TestDatabase
{
    private static Card NewCard(string number = "4111111111111111") => new()
    {
        CardNumber = number,
        AccountNo = "ACC1",
        CardType = "Debit",
        ExpiryDate = "1228",
        Cvv = "123",
        ContactlessAllowed = true,
        ContactlessLimit = 750m
    };

    private async Task<int> SeedCardAsync(string number = "4111111111111111")
    {
        var card = NewCard(number);
        await SeedAsync(card);
        return card.Id;
    }

    [Fact]
    public async Task GetAll_ReturnsAllCards()
    {
        await SeedCardAsync("1111");
        await SeedCardAsync("2222");

        var result = await new CardsController(NewContext()).GetAll();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Get_ExistingCard_ReturnsIt()
    {
        var id = await SeedCardAsync();

        var result = await new CardsController(NewContext()).Get(id);

        Assert.Equal("4111111111111111", result.Value!.CardNumber);
    }

    [Fact]
    public async Task Get_MissingCard_ReturnsNotFound()
    {
        var result = await new CardsController(NewContext()).Get(999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetByNumber_ExistingCard_ReturnsIt()
    {
        var id = await SeedCardAsync();

        var result = await new CardsController(NewContext()).GetByNumber("4111111111111111");

        Assert.Equal(id, result.Value!.Id);
    }

    [Fact]
    public async Task GetByNumber_MissingCard_ReturnsNotFound()
    {
        var result = await new CardsController(NewContext()).GetByNumber("0000");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_NewCard_AssignsIdAndIgnoresTransactionFields()
    {
        var card = NewCard();
        card.Id = 42;
        card.LastTransactionDate = DateTime.UtcNow;
        card.LastTransactionAmount = 99m;

        var result = await new CardsController(NewContext()).Create(card);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.IsType<Card>(created.Value);
        var stored = (await NewContext().Cards.FindAsync(saved.Id))!;
        Assert.Null(stored.LastTransactionDate);
        Assert.Null(stored.LastTransactionAmount);
    }

    [Fact]
    public async Task Create_DuplicateCardNumber_ReturnsConflict()
    {
        await SeedCardAsync();

        var result = await new CardsController(NewContext()).Create(NewCard());

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ChangesEditableFieldsButNotTransactionFields()
    {
        var id = await SeedCardAsync();
        var input = NewCard("5555444433332222");
        input.CardType = "Credit";
        input.ContactlessAllowed = false;
        input.LastTransactionAmount = 123m;

        var result = await new CardsController(NewContext()).Update(id, input);

        Assert.IsType<NoContentResult>(result);
        var saved = (await NewContext().Cards.FindAsync(id))!;
        Assert.Equal("5555444433332222", saved.CardNumber);
        Assert.Equal("Credit", saved.CardType);
        Assert.False(saved.ContactlessAllowed);
        Assert.Null(saved.LastTransactionAmount);
    }

    [Fact]
    public async Task Update_ToAnotherCardsNumber_ReturnsConflict()
    {
        await SeedCardAsync("1111");
        var id = await SeedCardAsync("2222");

        var result = await new CardsController(NewContext()).Update(id, NewCard("1111"));

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Update_MissingCard_ReturnsNotFound()
    {
        var result = await new CardsController(NewContext()).Update(999, NewCard());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ExistingCard_RemovesIt()
    {
        var id = await SeedCardAsync();

        var result = await new CardsController(NewContext()).Delete(id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await NewContext().Cards.FindAsync(id));
    }

    [Fact]
    public async Task Delete_MissingCard_ReturnsNotFound()
    {
        var result = await new CardsController(NewContext()).Delete(999);

        Assert.IsType<NotFoundResult>(result);
    }
}
