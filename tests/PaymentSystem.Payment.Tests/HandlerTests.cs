using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Events;
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

        public CardSecret? StoredSecret { get; private set; }

        public Task<WriteResult> CreateAsync(Card card, CardSecret secret)
        {
            if (Stored?.CardNumber == card.CardNumber)
                return Task.FromResult(WriteResult.Duplicate);
            card.Id = 7;
            Stored = card;
            StoredSecret = secret;
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
        var handler = new CardHandler(repository, TestSecrets.Hasher, logger);
        var card = new Card { CardNumber = Pan, AccountNo = 1000000001 };

        var (created, issued) = await handler.CreateAsync(card);
        Assert.Equal(WriteResult.Ok, created);
        Assert.Equal(TestSecrets.Hasher.HashCvv(Pan, issued!.Cvv), repository.StoredSecret!.CvvHash);
        Assert.Equal(TestSecrets.Hasher.HashPin(Pan, issued.Pin), repository.StoredSecret.PinHash);
        Assert.Equal((WriteResult.Duplicate, null), await handler.CreateAsync(new Card { CardNumber = Pan, AccountNo = 1000000001 }));
        Assert.Same(card, await handler.GetByNumberAsync(Pan));
        Assert.Equal(WriteResult.NotFound, await handler.UpdateAsync(99, card));
        Assert.Equal(WriteResult.CardNumberChanged, await handler.UpdateAsync(7, new Card { CardNumber = "5555444433332222", AccountNo = 1000000001 }));
        Assert.Equal(WriteResult.Ok, await handler.UpdateAsync(7, new Card { CardNumber = Pan, AccountNo = 1000000002 }));
        Assert.True(await handler.DeleteAsync(7));

        Assert.Contains(logger.Messages, m => m.StartsWith("CreateAsync: card PAN 411111********11 account 1000000001 -> Ok (id 7)"));
        Assert.Contains(logger.Messages, m => m.StartsWith("CreateAsync:") && m.Contains("-> Duplicate"));
        Assert.Contains(logger.Messages, m => m.StartsWith("GetByNumberAsync: card PAN 411111********11 found"));
        Assert.Contains(logger.Messages, m => m.StartsWith("UpdateAsync: card 99") && m.Contains("-> NotFound"));
        Assert.Contains(logger.Messages, m => m == "DeleteAsync: card 7 deleted");
        Assert.Contains(logger.Messages, m => m.StartsWith("UpdateAsync: card 7") && m.Contains("-> CardNumberChanged"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Pan));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(issued.Pin));
    }

    // İşlem akışının repository'si: tekrar gelen isteğin önceki kararı ayarlanabilir; oturumda ne yapıldığı kaydedilir.
    private sealed class FakeTransactionRepository : ITransactionRepository
    {
        public bool Duplicate { get; init; }
        public string? PreviousResponseCode { get; init; }
        public FakeSession? Session { get; private set; }

        public Task<ITransactionSession> BeginAsync() => Task.FromResult<ITransactionSession>(Session = new FakeSession(!Duplicate));
        public Task<PreviousResponse?> GetPreviousAsync(Guid id) =>
            Task.FromResult<PreviousResponse?>(PreviousResponseCode is null ? null : new(PreviousResponseCode, 1000000001));
    }

    private sealed class FakeSession(bool insertSucceeds) : ITransactionSession
    {
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public string? ResponseCode { get; private set; }

        public Task<bool> TryInsertAsync(DebitTransaction transaction) => Task.FromResult(insertSucceeds);
        public Task<bool> TryInsertAsync(CreditTransaction transaction) => Task.FromResult(insertSucceeds);
        public Task<Merchant?> GetMerchantAsync(string merchantId) =>
            Task.FromResult<Merchant?>(new Merchant { MerchantId = merchantId, Name = merchantId, MerchantType = "5399", Status = "Active" });
        public Task<Card?> GetCardAsync(string cardNumber) =>
            Task.FromResult<Card?>(new Card { CardNumber = cardNumber, AccountNo = 1000000001, ExpiryDate = "1299" });
        public Task<byte[]?> GetCvvHashAsync(string cardNumber) => Task.FromResult<byte[]?>(null);
        public Task<BankAccount?> GetAccountAsync(long accountNo) =>
            Task.FromResult<BankAccount?>(new BankAccount { AccountNo = accountNo, AccountStatus = "Active", Email = "musteri@example.com" });
        public Task<bool> TryDebitAsync(long accountNo, decimal amount) => Task.FromResult(true);
        public Task<bool> TryTransferAsync(long fromAccountNo, long toAccountNo, decimal amount) => Task.FromResult(true);
        public Task<CreditCardLimit?> GetCreditLimitAsync(string cardNumber) => Task.FromResult<CreditCardLimit?>(null);
        public Task<bool> TryUseCreditLimitAsync(string cardNumber, decimal amount) => Task.FromResult(true);
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
    private RecordingEventPublisher Events { get; } = new();

    private TransactionHandler TransactionHandler(ITransactionRepository repository, bool knownType = true) => new(
        repository,
        TestParameters.Create(new FakeParameterRepository
        {
            Routes = knownType ? [new TransactionRoute("0200", "000000", "10", "11", "Sale")] : [],
            ResponseCodes = [new("0000", "Approved", "00"), new("1001", "Transaction type not defined", "12")]
        }),
        new ExchangeRateCache(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System, Options.Create(new ExchangeRateOptions())),
        TestSecrets.Hasher,
        Events,
        TimeProvider.System,
        new ListLogger<TransactionHandler>());

    private static TransactionRequest Sale() => new()
    {
        Mti = "0200", CardNumber = Pan, ProcessingCode = "000000", AmountMinor = 15000, CurrencyCode = "949",
        TransmissionDateTime = "0315095959", Stan = "000001", LocalTime = "125959", LocalDate = "20260315", TerminalId = "TERM0001",
        MerchantId = "TRENDYOL       "
    };

    [Fact]
    public async Task TransactionHandler_Sale_DecidesWritesF39AndCommits()
    {
        var repository = new FakeTransactionRepository();

        Assert.Equal("00", await TransactionHandler(repository).ProcessAsync(Sale()));
        Assert.Equal("00", repository.Session!.ResponseCode);
        Assert.True(repository.Session.Committed);
        Assert.True(repository.Session.Disposed);

        var published = Assert.Single(Events.Events);
        Assert.Equal(("Sale", "00", "0000", 1000000001, "musteri@example.com", "411111********11"),
            (published.TransactionType, published.ResponseCode, published.Irc, published.AccountNo, published.Email, published.MaskedCardNumber));
        Assert.Equal((TransactionCompleted.Type, 1), (published.EventType, published.Version));
    }

    [Fact]
    public async Task TransactionHandler_Duplicate_ReturnsPreviousCodeWithoutCommitting()
    {
        var repository = new FakeTransactionRepository { Duplicate = true, PreviousResponseCode = "51" };

        var ex = await Assert.ThrowsAsync<TransactionException>(() => TransactionHandler(repository).ProcessAsync(Sale()));
        Assert.Equal(("51", "Duplicate request: Declined with F39 51", 1000000001), (ex.ResponseCode, ex.Description, ex.AccountNo));
        Assert.Null(repository.Session!.ResponseCode);
        Assert.False(repository.Session.Committed);
        Assert.True(repository.Session.Disposed);
        Assert.Empty(Events.Events);        // ilk istek olayını zaten yayımladı
    }

    [Fact]
    public async Task TransactionHandler_UnknownType_Returns12WithoutOpeningSession()
    {
        var repository = new FakeTransactionRepository();

        var ex = await Assert.ThrowsAsync<TransactionException>(() => TransactionHandler(repository, knownType: false).ProcessAsync(Sale()));
        Assert.Equal(("12", "1001", "Transaction type not defined"), (ex.ResponseCode, ex.Irc, ex.Description));
        Assert.Null(repository.Session);
        Assert.Empty(Events.Events);        // kayıt açılmadı, olay da yok
    }
}
