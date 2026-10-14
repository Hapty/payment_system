using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

public class CardsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private CardRepository Repository => new(Db);
    private CardsController Controller => new(new CardHandler(Repository, TestSecrets.Hasher, NullLogger<CardHandler>.Instance));

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
        await Repository.CreateAsync(card, new CardSecret(number, TestSecrets.Hasher.HashCvv(number, "123"), TestSecrets.Hasher.HashPin(number, "1234")));
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
        var saved = Assert.IsType<IssuedCard>(created.Value);
        Assert.NotEqual(42, saved.Id);
        var stored = (await Repository.GetAsync(saved.Id))!;
        Assert.Null(stored.LastTransactionDate);
        Assert.Null(stored.LastTransactionAmount);
    }

    [Fact]
    public async Task Create_ReturnsCvvAndPinOnceAndStoresOnlyTheirHashes()
    {
        var created = Assert.IsType<CreatedAtActionResult>((await Controller.Create(NewCard())).Result);
        var issued = Assert.IsType<IssuedCard>(created.Value);

        Assert.Matches("^[0-9]{3}$", issued.Cvv);
        Assert.Matches("^[0-9]{4}$", issued.Pin);
        Assert.Equal(("4111111111111111", "ACC1", "Debit", "1228"), (issued.CardNumber, issued.AccountNo, issued.CardType, issued.ExpiryDate));

        // CardSecret'ta düz değer değil, HMAC'leri durur.
        await using var connection = Db.Create();
        var (cvvHash, pinHash) = await connection.QuerySingleAsync<(byte[], byte[])>(
            "SELECT CvvHash, PinHash FROM dbo.CardSecret WHERE CardNumber = '4111111111111111'");
        Assert.Equal(TestSecrets.Hasher.HashCvv("4111111111111111", issued.Cvv), cvvHash);
        Assert.Equal(TestSecrets.Hasher.HashPin("4111111111111111", issued.Pin), pinHash);

        // GET kartın kendisini (Card) döner, CVV / PIN alanı yoktur; Card tablosunda da kolonları yoktur.
        Assert.IsNotType<IssuedCard>((await Controller.Get(issued.Id)).Value);
        Assert.Null(await connection.ExecuteScalarAsync<int?>("SELECT COL_LENGTH('dbo.Card', 'Cvv')"));
    }

    [Fact]
    public async Task Create_WithoutExpiry_StillGetsCvvAndPin()
    {
        var card = NewCard();
        card.ExpiryDate = null;

        var issued = Assert.IsType<IssuedCard>(Assert.IsType<CreatedAtActionResult>((await Controller.Create(card)).Result).Value);

        Assert.Matches("^[0-9]{3}$", issued.Cvv);
        Assert.Matches("^[0-9]{4}$", issued.Pin);
    }

    [Fact]
    public async Task Create_DuplicateCardNumber_KeepsTheFirstCardsSecret()
    {
        await SeedCardAsync();
        await using var connection = Db.Create();
        var before = await connection.QuerySingleAsync<byte[]>("SELECT CvvHash FROM dbo.CardSecret");

        Assert.IsType<ConflictObjectResult>((await Controller.Create(NewCard())).Result);

        Assert.Equal(before, await connection.QuerySingleAsync<byte[]>("SELECT CvvHash FROM dbo.CardSecret"));
    }

    [Fact]
    public async Task Delete_RemovesTheCardsSecretToo()
    {
        var id = await SeedCardAsync();

        Assert.IsType<NoContentResult>(await Controller.Delete(id));

        await using var connection = Db.Create();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CardSecret"));
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

        var input = NewCard();
        input.CardType = "Credit";
        input.ContactlessAllowed = false;
        input.ContactlessLimit = null;
        input.LastTransactionAmount = 123m;

        Assert.IsType<NoContentResult>(await Controller.Update(id, input));

        var saved = (await Repository.GetAsync(id))!;
        Assert.Equal(("4111111111111111", "Credit"), (saved.CardNumber, saved.CardType));
        Assert.False(saved.ContactlessAllowed);
        Assert.Null(saved.ContactlessLimit);
        Assert.Equal(10.50m, saved.LastTransactionAmount);   // CRUD ile değişmedi
    }

    [Theory]
    [InlineData("5555444433332222")]    // yeni bir numara
    [InlineData("1111")]                // başka bir kartın numarası
    public async Task Update_ChangingCardNumber_ReturnsBadRequestAndChangesNothing(string newNumber)
    {
        await SeedCardAsync("1111");
        var id = await SeedCardAsync("2222");
        var input = NewCard(newNumber);
        input.CardType = "Credit";

        Assert.IsType<BadRequestObjectResult>(await Controller.Update(id, input));

        var saved = (await Repository.GetAsync(id))!;
        Assert.Equal(("2222", "Debit"), (saved.CardNumber, saved.CardType));
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
