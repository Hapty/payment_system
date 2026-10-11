using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;
using PaymentSystem.Payment.Transactions;

namespace PaymentSystem.Payment.Tests;

// Handler'lar repository arayüzlerine bağlı olduğu için DB olmadan, sahte repository ile test edilir.
public class HandlerTests
{
    private const string Pan = "4111111111111111";

    private sealed class FakeCardRepository : ICardRepository
    {
        public Card? Stored { get; set; }

        public Task<IReadOnlyList<Card>> GetAllAsync() => Task.FromResult<IReadOnlyList<Card>>(Stored is null ? [] : [Stored]);
        public Task<Card?> GetAsync(int id) => Task.FromResult(Stored?.Id == id ? Stored : null);
        public Task<Card?> GetByNumberAsync(string cardNumber) => Task.FromResult(Stored?.CardNumber == cardNumber ? Stored : null);

        public Task<WriteResult> CreateAsync(Card card)
        {
            if (Stored?.CardNumber == card.CardNumber)
                return Task.FromResult(WriteResult.Duplicate);
            card.Id = 7;
            Stored = card;
            return Task.FromResult(WriteResult.Ok);
        }

        public Task<WriteResult> UpdateAsync(int id, Card input) => Task.FromResult(Stored?.Id == id ? WriteResult.Ok : WriteResult.NotFound);
        public Task<bool> DeleteAsync(int id) => Task.FromResult(Stored?.Id == id);
    }

    [Fact]
    public async Task CardHandler_PassesRepositoryResultsThroughAndLogsMaskedPan()
    {
        var repository = new FakeCardRepository();
        var logger = new ListLogger<CardHandler>();
        var handler = new CardHandler(repository, logger);
        var card = new Card { CardNumber = Pan, AccountNo = "ACC1" };

        Assert.Equal(WriteResult.Ok, await handler.CreateAsync(card));
        Assert.Equal(WriteResult.Duplicate, await handler.CreateAsync(new Card { CardNumber = Pan, AccountNo = "ACC1" }));
        Assert.Same(card, await handler.GetByNumberAsync(Pan));
        Assert.Equal(WriteResult.NotFound, await handler.UpdateAsync(99, card));
        Assert.True(await handler.DeleteAsync(7));

        Assert.Contains(logger.Messages, m => m.StartsWith("CreateAsync: card PAN 411111********11 account ACC1 -> Ok (id 7)"));
        Assert.Contains(logger.Messages, m => m.StartsWith("CreateAsync:") && m.Contains("-> Duplicate"));
        Assert.Contains(logger.Messages, m => m.StartsWith("GetByNumberAsync: card PAN 411111********11 found"));
        Assert.Contains(logger.Messages, m => m.StartsWith("UpdateAsync: card 99") && m.Contains("-> NotFound"));
        Assert.Contains(logger.Messages, m => m == "DeleteAsync: card 7 deleted");
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Pan));
    }

    // İşlem akışının repository'si: tekrar gelen isteğin önceki kararı ayarlanabilir; oturumda ne yapıldığı kaydedilir.
    private sealed class FakeTransactionRepository : ITransactionRepository
    {
        public bool Duplicate { get; init; }
        public string? PreviousResponseCode { get; init; }
        public FakeSession? Session { get; private set; }

        public Task<IDebitSession> BeginAsync() => Task.FromResult<IDebitSession>(Session = new FakeSession(!Duplicate));
        public Task<string?> GetResponseCodeAsync(Guid id) => Task.FromResult(PreviousResponseCode);
    }

    private sealed class FakeSession(bool insertSucceeds) : IDebitSession
    {
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public string? ResponseCode { get; private set; }

        public Task<bool> TryInsertAsync(DebitTransaction transaction) => Task.FromResult(insertSucceeds);
        public Task<Card?> GetCardAsync(string cardNumber) =>
            Task.FromResult<Card?>(new Card { CardNumber = cardNumber, AccountNo = "ACC1", ExpiryDate = "1299" });
        public Task<string?> GetAccountStatusAsync(string accountNo) => Task.FromResult<string?>("Active");
        public Task<bool> TryDebitAsync(string accountNo, decimal amount) => Task.FromResult(true);
        public Task SetCardLastTransactionAsync(string cardNumber, decimal amount) => Task.CompletedTask;

        public Task SetResponseCodeAsync(Guid id, string responseCode)
        {
            ResponseCode = responseCode;
            return Task.CompletedTask;
        }

        public Task CommitAsync()
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // knownType false: 0200/000000 tanım tablosunda yok.
    private static TransactionHandler TransactionHandler(ITransactionRepository repository, bool knownType = true) => new(
        repository,
        TestParameters.Create(new FakeParameterRepository
        {
            Routes = knownType ? [new TransactionRoute("0200", "000000", "10", "11", "Sale")] : []
        }),
        new ExchangeRateCache(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System, Options.Create(new ExchangeRateOptions())),
        TimeProvider.System,
        new ListLogger<TransactionHandler>());

    private static TransactionRequest Sale() => new()
    {
        Mti = "0200", CardNumber = Pan, ProcessingCode = "000000", AmountMinor = 15000, CurrencyCode = "949",
        TransmissionDateTime = "0315095959", Stan = "000001", LocalTime = "125959", LocalDate = "0315", TerminalId = "TERM0001"
    };

    [Fact]
    public async Task TransactionHandler_Sale_DecidesWritesF39AndCommits()
    {
        var repository = new FakeTransactionRepository();

        Assert.Equal(ResponseCodes.Approved, await TransactionHandler(repository).ProcessAsync(Sale()));
        Assert.Equal(ResponseCodes.Approved, repository.Session!.ResponseCode);
        Assert.True(repository.Session.Committed);
        Assert.True(repository.Session.Disposed);
    }

    [Fact]
    public async Task TransactionHandler_Duplicate_ReturnsPreviousCodeWithoutCommitting()
    {
        var repository = new FakeTransactionRepository { Duplicate = true, PreviousResponseCode = "51" };

        Assert.Equal("51", await TransactionHandler(repository).ProcessAsync(Sale()));
        Assert.Null(repository.Session!.ResponseCode);
        Assert.False(repository.Session.Committed);
        Assert.True(repository.Session.Disposed);
    }

    [Fact]
    public async Task TransactionHandler_UnknownType_Returns12WithoutOpeningSession()
    {
        var repository = new FakeTransactionRepository();

        Assert.Equal(ResponseCodes.InvalidTransaction, await TransactionHandler(repository, knownType: false).ProcessAsync(Sale()));
        Assert.Null(repository.Session);
    }
}
