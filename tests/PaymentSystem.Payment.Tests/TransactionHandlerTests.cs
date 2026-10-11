using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;
using PaymentSystem.Payment.Transactions;

namespace PaymentSystem.Payment.Tests;

public class TransactionHandlerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private const string Pan = "4111111111111111";
    private const string AccountNo = "ACC1";

    // Testler takvime bağlı olmasın diye "bugün" sabit: 15 Mart 2026.
    private FixedClock Clock { get; } = new(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));

    // Testteki kur cache'i: TCMB'ye gidilmez, kurları test kendisi yazar (StoreRates).
    private ExchangeRateCache? _rates;
    private ExchangeRateCache Rates => _rates ??= new(new MemoryCache(new MemoryCacheOptions()), Clock, Options.Create(new ExchangeRateOptions()));

    // Tanım cache'i her test için bir kez, ilk işlemde DB'den (testin seed'inden) yüklenir.
    private ParameterCache? _parameters;
    private ParameterCache Parameters => _parameters ??= TestParameters.Create(Db, Clock);

    private TransactionHandler Handler => new(new TransactionRepository(Db), Parameters, Rates, Clock, NullLogger<TransactionHandler>.Instance);

    private void StoreRates(params (string Code, decimal Rate)[] rates) =>
        Rates.Store(new TcmbBulletin(new DateOnly(2026, 3, 13), rates.ToDictionary(r => r.Code, r => r.Rate)));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record StoredTransaction(
        string Mti, string F2_CardNo, string Otc, string Ots, decimal F4_Amount, decimal? LocalTxnAmount,
        string F12_TransactionTime, string F13_TransactionDate, string F43_Description, string? F39_ResponseCode);

    // Her test DB'yi boşalttığı için seed verisini (02_seed_transaction_types.sql, 03_seed_entry_modes.sql, 04_seed_internal_response_codes.sql) ve bir hesap + kartı yeniden ekler.
    private async Task SeedAsync(decimal balance = 1000m, string status = "Active", string? expiry = "1228",
        bool contactless = true, decimal? contactlessLimit = 750m)
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES ('10', '11', 'Sale'), ('11', '11', 'MoneyTransfer'), ('40', '10', 'BalanceInquiry');
            INSERT INTO dbo.MtiProcessingCode (Mti, F3_ProcessingCode, Otc, Ots)
                VALUES ('0200', '000000', '10', '11'), ('0200', '400000', '11', '11'), ('0200', '310000', '40', '10');
            INSERT INTO dbo.EntryMode (Code, Name, IsContactless)
                VALUES ('00', 'Unknown', 0), ('01', 'Manual', 0), ('02', 'MagneticStripe', 0), ('05', 'Chip', 0),
                       ('07', 'ContactlessChip', 1), ('90', 'MagneticStripeFullTrack', 0), ('91', 'ContactlessMagneticStripe', 1);
            INSERT INTO dbo.InternalResponseCode (Irc, Description, F39_ResponseCode) VALUES ('0000', 'Approved', '00'), ('1001', 'Transaction type not defined (MTI + F3)', '12'), ('1002', 'Entry mode (F22) not defined', '12'), ('1003', 'Transaction type not supported', '12'), ('1004', 'Currency not supported', '12'), ('2001', 'Card not found', '14'), ('2002', 'Card expired or F14 does not match', '54'), ('2003', 'Account not active', '62'), ('2004', 'Contactless not allowed for card', '57'), ('2005', 'Contactless limit exceeded', '65'), ('3001', 'Amount is zero', '13'), ('3002', 'Insufficient funds', '51'), ('9001', 'Exchange rate unavailable or stale', '91');
            INSERT INTO dbo.BankAccount (AccountNo, AccountStatus, Balance) VALUES (@AccountNo, @status, @balance);
            INSERT INTO dbo.Card (CardNumber, AccountNo, ExpiryDate, ContactlessAllowed, ContactlessLimit)
                VALUES (@Pan, @AccountNo, @expiry, @contactless, @contactlessLimit);
            """,
            new { AccountNo, Pan, status, balance, expiry, contactless, contactlessLimit });
    }

    private static TransactionRequest Sale(long amountMinor = 15000, string localTime = "125959") => new()
    {
        Mti = "0200",
        CardNumber = Pan,
        ProcessingCode = "000000",
        AmountMinor = amountMinor,
        CurrencyCode = "949",
        TransmissionDateTime = "0315095959",
        Stan = "000001",
        LocalTime = localTime,
        LocalDate = "0315",
        TerminalId = "TERM0001",
        CardExpiry = "2812",
        MerchantType = "5999",
        EntryMode = "051",
        Rrn = "260315000001",
        MerchantId = "MERCHANT0000001",
        CardAcceptor = "PAYMENT SYSTEM POS SIMULATOR".PadRight(40)
    };

    // Son reddin exception'ı; testler F39'u karşılaştırır, alanları gerektiğinde buradan kontrol eder.
    private TransactionException? LastFailure { get; set; }

    // Onayda F39 döner; red / teknik hatada TransactionException'ın F39'u.
    private async Task<string> AuthorizeAsync(TransactionRequest request)
    {
        try
        {
            return await Handler.ProcessAsync(request);
        }
        catch (TransactionException ex)
        {
            LastFailure = ex;
            return ex.ResponseCode;
        }
    }

    private async Task<Guid> StoredIdAsync()
    {
        await using var connection = Db.Create();
        return await connection.ExecuteScalarAsync<Guid>("SELECT Guid FROM dbo.DebitTransaction");
    }

    private async Task<decimal> BalanceAsync()
    {
        await using var connection = Db.Create();
        return await connection.ExecuteScalarAsync<decimal>("SELECT Balance FROM dbo.BankAccount WHERE AccountNo = @AccountNo", new { AccountNo });
    }

    private async Task<IReadOnlyList<StoredTransaction>> TransactionsAsync()
    {
        await using var connection = Db.Create();
        return (await connection.QueryAsync<StoredTransaction>(
            """
            SELECT Mti, F2_CardNo, Otc, Ots, F4_Amount, LocalTxnAmount, F12_TransactionTime, F13_TransactionDate,
                   F43_Description, F39_ResponseCode
            FROM dbo.DebitTransaction
            """)).AsList();
    }

    [Fact]
    public async Task Sale_WithEnoughBalance_IsApprovedDebitsAccountAndRecordsTransaction()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));

        Assert.Equal(850m, await BalanceAsync());
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Equal(("0200", Pan, "10", "11", 150m, 150m), (stored.Mti, stored.F2_CardNo, stored.Otc, stored.Ots, stored.F4_Amount, stored.LocalTxnAmount));
        Assert.Equal(("125959", "20260315", "PAYMENT SYSTEM POS SIMULATOR", "00"),
            (stored.F12_TransactionTime, stored.F13_TransactionDate, stored.F43_Description, stored.F39_ResponseCode));

        await using var connection = Db.Create();
        Assert.Equal(150m, await connection.ExecuteScalarAsync<decimal>("SELECT LastTransactionAmount FROM dbo.Card WHERE CardNumber = @Pan", new { Pan }));
    }

    [Fact]
    public async Task Sale_WithExactBalance_IsApproved()
    {
        await SeedAsync(balance: 150m);

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task Sale_OverBalance_IsDeclinedAndRecordedWithoutDebit()
    {
        await SeedAsync(balance: 100m);

        Assert.Equal("51", await AuthorizeAsync(Sale(15000)));

        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal("51", Assert.Single(await TransactionsAsync()).F39_ResponseCode);
    }

    [Fact]
    public async Task DuplicateRequest_ReturnsFirstAnswerAndDebitsOnce()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));
        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));

        Assert.Equal(850m, await BalanceAsync());
        Assert.Single(await TransactionsAsync());
    }

    [Fact]
    public async Task DuplicateOfDeclinedRequest_StaysDeclinedEvenAfterTopUp()
    {
        await SeedAsync(balance: 100m);
        Assert.Equal("51", await AuthorizeAsync(Sale(15000)));

        await using (var connection = Db.Create())
            await connection.ExecuteAsync("UPDATE dbo.BankAccount SET Balance = 1000");

        Assert.Equal("51", await AuthorizeAsync(Sale(15000)));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicates_AreDecidedOnlyOnce()
    {
        await SeedAsync();

        var codes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AuthorizeAsync(Sale(15000))));

        Assert.All(codes, code => Assert.Equal("00", code));
        Assert.Equal(850m, await BalanceAsync());
        Assert.Single(await TransactionsAsync());
    }

    [Fact]
    public async Task ConcurrentSales_CannotOverdrawAccount()
    {
        await SeedAsync(balance: 300m);

        // Farklı saniyeler: hepsi ayrı işlem. 100'lük 5 satıştan sadece 3'ü bakiyeye sığar.
        var codes = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => AuthorizeAsync(Sale(10000, localTime: $"1259{i:D2}"))));

        Assert.Equal(3, codes.Count(c => c == "00"));
        Assert.Equal(2, codes.Count(c => c == "51"));
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task SameCardAndAmountInAnotherSecond_IsSeparateTransaction()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(Sale(15000, localTime: "125959")));
        Assert.Equal("00", await AuthorizeAsync(Sale(15000, localTime: "130000")));

        Assert.Equal(700m, await BalanceAsync());
        Assert.Equal(2, (await TransactionsAsync()).Count);
    }

    [Fact]
    public async Task DifferentAmountInSameSecond_IsSeparateTransaction()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));
        Assert.Equal("00", await AuthorizeAsync(Sale(10000)));

        Assert.Equal(750m, await BalanceAsync());
    }

    [Fact]
    public async Task DifferentTypeInSameSecond_IsSeparateTransaction()
    {
        await SeedAsync();
        var inquiry = Sale(15000);
        inquiry.ProcessingCode = "310000";

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));
        Assert.Equal("00", await AuthorizeAsync(inquiry));

        Assert.Equal(2, (await TransactionsAsync()).Count);
    }

    // Bilinen sınır: anahtarda STAN / terminal yok. Aynı kart + saniye + tutar, başka terminalden gelse de tekrar sayılır.
    [Fact]
    public async Task SameCardSecondAndAmountFromAnotherTerminal_IsTreatedAsDuplicate()
    {
        await SeedAsync();
        var other = Sale(15000);
        other.TerminalId = "TERM0002";
        other.Stan = "000002";

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));
        Assert.Equal("00", await AuthorizeAsync(other));

        Assert.Equal(850m, await BalanceAsync());
        Assert.Single(await TransactionsAsync());
    }

    [Fact]
    public async Task UnknownProcessingCode_IsInvalidTransactionAndNotRecorded()
    {
        await SeedAsync();
        var request = Sale();
        request.ProcessingCode = "990000";

        Assert.Equal("12", await AuthorizeAsync(request));
        Assert.Empty(await TransactionsAsync());
    }

    [Fact]
    public async Task UnknownCard_IsInvalidCardNumber()
    {
        await SeedAsync();
        var request = Sale();
        request.CardNumber = "5555444433332222";

        Assert.Equal("14", await AuthorizeAsync(request));
        Assert.Equal("14", Assert.Single(await TransactionsAsync()).F39_ResponseCode);
    }

    [Fact]
    public async Task ExpiredCard_IsDeclined()
    {
        await SeedAsync(expiry: "0226");    // Şubat 2026 sonu; bugün Mart 2026
        var request = Sale();
        request.CardExpiry = "2602";

        Assert.Equal("54", await AuthorizeAsync(request));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task CardExpiringThisMonth_IsStillValid()
    {
        await SeedAsync(expiry: "0326");
        var request = Sale();
        request.CardExpiry = "2603";

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task ExpiryNotMatchingCard_IsDeclined()
    {
        await SeedAsync(expiry: "1228");
        var request = Sale();
        request.CardExpiry = "2912";

        Assert.Equal("54", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task MissingExpiryInRequest_IsNotChecked()
    {
        await SeedAsync();
        var request = Sale();
        request.CardExpiry = null;

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task InactiveAccount_IsRestricted()
    {
        await SeedAsync(status: "Blocked");

        Assert.Equal("62", await AuthorizeAsync(Sale()));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task CardWithoutAccount_IsRestricted()
    {
        await SeedAsync();
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("DELETE FROM dbo.BankAccount");

        Assert.Equal("62", await AuthorizeAsync(Sale()));
    }

    [Fact]
    public async Task ZeroAmountSale_IsInvalidAmount()
    {
        await SeedAsync();

        Assert.Equal("13", await AuthorizeAsync(Sale(0)));
    }

    [Fact]
    public async Task ForeignCurrencySale_IsConvertedWithCachedSellingRateAndDebitedInLira()
    {
        await SeedAsync();
        StoreRates(("USD", 49.2152m));
        var request = Sale(1000);       // 10,00 USD
        request.CurrencyCode = "840";

        Assert.Equal("00", await AuthorizeAsync(request));

        // 10 x 49,2152 = 492,152 -> kuruşa yuvarlanır: 492,15 TL
        Assert.Equal(507.85m, await BalanceAsync());
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Equal((10m, 492.15m), (stored.F4_Amount, stored.LocalTxnAmount));
        await using var connection = Db.Create();
        Assert.Equal(492.15m, await connection.ExecuteScalarAsync<decimal>("SELECT LastTransactionAmount FROM dbo.Card WHERE CardNumber = @Pan", new { Pan }));
    }

    [Fact]
    public async Task ForeignCurrencySale_RoundsHalfKurusUp()
    {
        await SeedAsync();
        StoreRates(("EUR", 55.05m));
        var request = Sale(10);         // 0,10 EUR x 55,05 = 5,505 -> 5,51 (banker's rounding olsaydı 5,50)
        request.CurrencyCode = "978";

        Assert.Equal("00", await AuthorizeAsync(request));
        Assert.Equal(5.51m, Assert.Single(await TransactionsAsync()).LocalTxnAmount);
    }

    [Fact]
    public async Task ForeignCurrencySale_OverBalanceInLira_IsInsufficientFunds()
    {
        await SeedAsync(balance: 400m);
        StoreRates(("USD", 49.2152m));
        var request = Sale(1000);       // 492,15 TL > 400 TL
        request.CurrencyCode = "840";

        Assert.Equal("51", await AuthorizeAsync(request));
        Assert.Equal(400m, await BalanceAsync());
        Assert.Equal(492.15m, Assert.Single(await TransactionsAsync()).LocalTxnAmount);
    }

    [Fact]
    public async Task ForeignCurrencySale_ContactlessLimitIsCheckedInLira()
    {
        await SeedAsync();
        StoreRates(("USD", 49.2152m));
        var request = Sale(2000);       // 20 USD = 984,30 TL > 750 TL temassız limit
        request.CurrencyCode = "840";
        request.EntryMode = "071";

        Assert.Equal("65", await AuthorizeAsync(request));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task ForeignCurrencySale_WithoutCachedRate_IsDeclined91WithoutDebit()
    {
        await SeedAsync();
        var request = Sale();
        request.CurrencyCode = "840";

        Assert.Equal("91", await AuthorizeAsync(request));

        Assert.Equal(1000m, await BalanceAsync());
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Equal("91", stored.F39_ResponseCode);
        Assert.Null(stored.LocalTxnAmount);
    }

    [Fact]
    public async Task ForeignCurrencySale_WithStaleRate_IsDeclined91()
    {
        await SeedAsync();
        StoreRates(("USD", 49.2152m));
        Clock.Now = Clock.Now.AddHours(25);     // son başarılı çekimden bu yana MaxAgeHours (24) geçti
        var request = Sale();
        request.CurrencyCode = "840";
        request.LocalDate = "0316";

        Assert.Equal("91", await AuthorizeAsync(request));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task CurrencyWithoutTcmbRate_IsNotSupported()
    {
        await SeedAsync();
        StoreRates(("USD", 49.2152m));
        var request = Sale();
        request.CurrencyCode = "999";

        Assert.Equal("12", await AuthorizeAsync(request));
        Assert.Null(Assert.Single(await TransactionsAsync()).LocalTxnAmount);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Theory]
    [InlineData("071")]     // temassız çip
    [InlineData("911")]     // temassız manyetik şerit
    public async Task ContactlessSale_OnCardWithoutContactless_IsNotPermitted(string entryMode)
    {
        await SeedAsync(contactless: false);
        var request = Sale();
        request.EntryMode = entryMode;

        Assert.Equal("57", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task ContactlessSale_OverLimit_AsksForChipAndPin()
    {
        await SeedAsync(contactlessLimit: 100m);
        var request = Sale(15000);
        request.EntryMode = "071";

        Assert.Equal("65", await AuthorizeAsync(request));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task ContactlessSale_WithinLimit_IsApproved()
    {
        await SeedAsync(contactlessLimit: 750m);
        var request = Sale(15000);
        request.EntryMode = "071";

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task ChipSale_OverContactlessLimit_IsApproved()
    {
        await SeedAsync(contactless: false, contactlessLimit: 100m);

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));   // EntryMode 051 = çip
    }

    [Theory]
    [InlineData("991")]     // tabloda tanımlı değil
    [InlineData("07")]      // eksik hane
    public async Task Sale_WithUndefinedEntryMode_IsInvalidTransaction(string entryMode)
    {
        await SeedAsync();
        var request = Sale();
        request.EntryMode = entryMode;

        Assert.Equal("12", await AuthorizeAsync(request));
        Assert.Equal("12", Assert.Single(await TransactionsAsync()).F39_ResponseCode);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Sale_WithoutEntryMode_IsApprovedAsNotContactless()
    {
        await SeedAsync(contactless: false);
        var request = Sale();
        request.EntryMode = null;

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task ContactlessDecision_ComesFromEntryModeTable()
    {
        // 05 tabloda temassız olarak işaretlenirse çip işlemi de temassız kurallarına girer: kod değil tablo belirler.
        await SeedAsync(contactless: false);
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("UPDATE dbo.EntryMode SET IsContactless = 1 WHERE Code = '05'");

        Assert.Equal("57", await AuthorizeAsync(Sale()));   // EntryMode 051
    }

    [Fact]
    public async Task ResponseCode_ComesFromInternalResponseCodeTable()
    {
        // 3002 (bakiye yetersiz) tabloda 61'e çevrilirse POS 61 alır: F39'u kod değil tablo belirler.
        await SeedAsync(balance: 10m);
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("UPDATE dbo.InternalResponseCode SET F39_ResponseCode = '61' WHERE Irc = '3002'");

        Assert.Equal("61", await AuthorizeAsync(Sale()));
        Assert.Equal("61", Assert.Single(await TransactionsAsync()).F39_ResponseCode);
    }

    [Fact]
    public async Task UndefinedIrc_Responds96AndMovesNoMoney()
    {
        // Onay kodu (0000) tabloda yok: işlem onaylanmış sayılmaz, transaction geri alınmaz ama F39 96 yazılır.
        await SeedAsync();
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("DELETE FROM dbo.InternalResponseCode WHERE Irc = '0000'");

        Assert.Equal("96", await AuthorizeAsync(Sale()));
        Assert.Equal("96", Assert.Single(await TransactionsAsync()).F39_ResponseCode);
        Assert.Equal(("0000", "IRC 0000 is not defined", AccountNo), (LastFailure!.Irc, LastFailure.Description, LastFailure.AccountNo));
        Assert.True(LastFailure.IsTechnical);
    }

    [Fact]
    public async Task Decline_ThrowsTransactionExceptionAfterRecordIsCommitted()
    {
        await SeedAsync(balance: 10m);

        var ex = await Assert.ThrowsAsync<TransactionException>(() => Handler.ProcessAsync(Sale(15000)));

        Assert.Equal("51", ex.ResponseCode);
        Assert.Equal("3002", ex.Irc);
        Assert.Equal("Insufficient funds", ex.Description);
        Assert.Equal("411111********11", ex.CardNumber);
        Assert.Equal(await StoredIdAsync(), ex.TransactionId);
        Assert.Equal(AccountNo, ex.AccountNo);
        Assert.False(ex.IsTechnical);
        Assert.DoesNotContain(Pan, ex.Message);
        Assert.Equal("51", Assert.Single(await TransactionsAsync()).F39_ResponseCode);   // kayıt geri alınmadı
    }

    [Fact]
    public async Task CardNotFound_ExceptionHasNoAccount()
    {
        await SeedAsync();
        var request = Sale();
        request.CardNumber = "4000000000000002";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => Handler.ProcessAsync(request));

        Assert.Equal(("14", "2001", "Card not found"), (ex.ResponseCode, ex.Irc, ex.Description));
        Assert.Equal(await StoredIdAsync(), ex.TransactionId);
        Assert.Null(ex.AccountNo);
    }

    [Fact]
    public async Task UndefinedTransactionType_ExceptionHasNoTransactionId()
    {
        await SeedAsync();
        var request = Sale();
        request.ProcessingCode = "990000";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => Handler.ProcessAsync(request));

        Assert.Equal(("12", "1001"), (ex.ResponseCode, ex.Irc));
        Assert.Null(ex.TransactionId);
        Assert.Empty(await TransactionsAsync());
    }

    [Fact]
    public async Task DuplicateOfDeclinedRequest_ThrowsAgainWithAccount()
    {
        await SeedAsync(balance: 10m);
        await AuthorizeAsync(Sale(15000));

        var ex = await Assert.ThrowsAsync<TransactionException>(() => Handler.ProcessAsync(Sale(15000)));

        Assert.Equal("51", ex.ResponseCode);
        Assert.Null(ex.Irc);
        Assert.Equal("Duplicate request: Insufficient funds", ex.Description);
        Assert.Equal(await StoredIdAsync(), ex.TransactionId);
        Assert.Equal(AccountNo, ex.AccountNo);
    }

    [Fact]
    public async Task DatabaseUnreachable_ThrowsTechnical91WithCause()
    {
        var unreachable = new SqlConnectionFactory("Server=127.0.0.1,1;Database=PaymentSystem;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True");
        var handler = new TransactionHandler(new TransactionRepository(unreachable), TestParameters.Create(unreachable), Rates, Clock,
            NullLogger<TransactionHandler>.Instance);

        var ex = await Assert.ThrowsAsync<TransactionException>(() => handler.ProcessAsync(Sale()));

        Assert.Equal(("91", "System unavailable"), (ex.ResponseCode, ex.Description));
        Assert.True(ex.IsTechnical);
        Assert.NotNull(ex.InnerException);
        Assert.Equal("411111********11", ex.CardNumber);
    }

    [Fact]
    public async Task BalanceInquiry_IsApprovedWithoutChangingBalance()
    {
        await SeedAsync();
        var request = Sale(0);
        request.ProcessingCode = "310000";

        Assert.Equal("00", await AuthorizeAsync(request));

        Assert.Equal(1000m, await BalanceAsync());
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Equal(("40", "10"), (stored.Otc, stored.Ots));
    }

    [Fact]
    public async Task MoneyTransfer_IsNotSupportedYet()
    {
        await SeedAsync();
        var request = Sale();
        request.ProcessingCode = "400000";

        Assert.Equal("12", await AuthorizeAsync(request));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task DecemberDateReceivedInJanuary_IsStoredWithPreviousYear()
    {
        await SeedAsync(expiry: "1228");
        Clock.Now = new DateTimeOffset(2027, 1, 1, 0, 0, 5, TimeSpan.Zero);
        var request = Sale();
        request.LocalDate = "1231";

        await AuthorizeAsync(request);

        Assert.Equal("20261231", Assert.Single(await TransactionsAsync()).F13_TransactionDate);
    }

    [Fact]
    public async Task NextMonthDate_KeepsCurrentYear()
    {
        await SeedAsync();
        Clock.Now = new DateTimeOffset(2026, 3, 31, 23, 59, 0, TimeSpan.Zero);
        var request = Sale();
        request.LocalDate = "0401";     // POS'un saat dilimi UTC'nin ilerisinde

        await AuthorizeAsync(request);

        Assert.Equal("20260401", Assert.Single(await TransactionsAsync()).F13_TransactionDate);
    }
}
