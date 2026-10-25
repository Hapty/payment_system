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
    private const long AccountNo = 1000000001;
    private const string Email = "musteri@example.com";
    private const string MerchantId = "TRENDYOL";
    private const string MerchantType = "5399";

    // Testler takvime bağlı olmasın diye "bugün" sabit: 15 Mart 2026.
    private FixedClock Clock { get; } = new(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));

    // Testteki kur cache'i: TCMB'ye gidilmez, kurları test kendisi yazar (StoreRates).
    private ExchangeRateCache? _rates;
    private ExchangeRateCache Rates => _rates ??= new(new MemoryCache(new MemoryCacheOptions()), Clock, Options.Create(new ExchangeRateOptions()));

    // Tanım cache'i her test için bir kez, ilk işlemde DB'den (testin seed'inden) yüklenir.
    private ParameterCache? _parameters;
    private ParameterCache Parameters => _parameters ??= TestParameters.Create(Db, Clock);

    // Yayımlanan olaylar Kafka yerine burada toplanır.
    private RecordingEventPublisher Events { get; } = new();

    private TransactionHandler Handler => new(new TransactionRepository(Db), Parameters, Rates, TestSecrets.Hasher, Events, Clock, NullLogger<TransactionHandler>.Instance);

    private void StoreRates(params (string Code, decimal Rate)[] rates) =>
        Rates.Store(new TcmbBulletin(new DateOnly(2026, 3, 13), rates.ToDictionary(r => r.Code, r => r.Rate)));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record StoredTransaction(
        string Mti, string F2_CardNo, string Otc, string Ots, decimal F4_Amount, decimal? LocalTxnAmount,
        string F12_TransactionTime, string F13_TransactionDate, string? F42_MerchantId, string F43_Description, string? F39_ResponseCode);

    // Her test DB'yi boşalttığı için seed verisini (02_seed_transaction_types.sql, 03_seed_entry_modes.sql, 04_seed_internal_response_codes.sql) ve bir hesap + kartı yeniden ekler.
    private async Task SeedAsync(decimal balance = 1000m, string status = "Active", string? expiry = "1228",
        bool contactless = true, decimal? contactlessLimit = 750m, string? email = Email, bool withSecret = true,
        string merchantStatus = "Active", string? cardType = null, string cardStatus = "Active")
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES ('10', '11', 'Sale'), ('11', '11', 'MoneyTransfer'), ('40', '10', 'BalanceInquiry');
            INSERT INTO dbo.MtiProcessingCode (Mti, F3_ProcessingCode, Otc, Ots)
                VALUES ('0200', '000000', '10', '11'), ('0200', '400000', '11', '11'), ('0200', '310000', '40', '10');
            INSERT INTO dbo.EntryMode (Code, Name, IsContactless)
                VALUES ('00', 'Unknown', 0), ('01', 'Manual', 0), ('02', 'MagneticStripe', 0), ('05', 'Chip', 0),
                       ('07', 'ContactlessChip', 1), ('90', 'MagneticStripeFullTrack', 0), ('91', 'ContactlessMagneticStripe', 1), ('81', 'ECommerce', 0);
            INSERT INTO dbo.InternalResponseCode (Irc, Description, F39_ResponseCode) VALUES ('0000', 'Approved', '00'), ('1001', 'Transaction type not defined (MTI + F3)', '12'), ('1002', 'Entry mode (F22) not defined', '12'), ('1003', 'Transaction type not supported', '12'), ('1004', 'Currency not supported', '12'), ('1005', 'Recipient card (F48) missing or invalid', '30'), ('1006', 'Transfer to the same account', '12'), ('1007', 'Merchant ID (F42) missing', '30'), ('1008', 'Installments not allowed for this transaction', '57'), ('2001', 'Card not found', '14'), ('2002', 'Card expired or F14 does not match', '54'), ('2003', 'Account not active', '62'), ('2004', 'Contactless not allowed for card', '57'), ('2005', 'Contactless limit exceeded', '65'), ('2006', 'CVV2 missing or does not match', 'N7'), ('2007', 'Recipient card not found', '14'), ('2008', 'Recipient account not active', '62'), ('2009', 'Card not active', '62'), ('3001', 'Amount is zero', '13'), ('3002', 'Insufficient funds', '51'), ('3003', 'Credit limit not defined', '57'), ('4001', 'Merchant not found', '03'), ('4002', 'Merchant not active', '03'), ('4003', 'Merchant type (F18) does not match merchant', '03'), ('9001', 'Exchange rate unavailable or stale', '91');
            INSERT INTO dbo.Merchant (MerchantId, Name, MerchantType, Status) VALUES (@MerchantId, 'TRENDYOL', @MerchantType, @merchantStatus);
            INSERT INTO dbo.BankAccount (AccountNo, AccountStatus, Balance, Email) VALUES (@AccountNo, @status, @balance, @email);
            INSERT INTO dbo.Card (CardNumber, AccountNo, CardType, ExpiryDate, ContactlessAllowed, ContactlessLimit, Status)
                VALUES (@Pan, @AccountNo, @cardType, @expiry, @contactless, @contactlessLimit, @cardStatus);
            """,
            new { AccountNo, Pan, status, balance, expiry, contactless, contactlessLimit, email, MerchantId, MerchantType, merchantStatus, cardType, cardStatus });
        if (withSecret)
            await connection.ExecuteAsync(
                "INSERT INTO dbo.CardSecret (CardNumber, CvvHash, PinHash) VALUES (@Pan, @cvvHash, @pinHash)",
                new { Pan, cvvHash = TestSecrets.Hasher.HashCvv(Pan, CardCvv), pinHash = TestSecrets.Hasher.HashPin(Pan, "1234") });
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
        LocalDate = "20260315",
        TerminalId = "TERM0001",
        CardExpiry = "2812",
        MerchantType = MerchantType,
        EntryMode = "051",
        Rrn = "260315000001",
        MerchantId = MerchantId.PadRight(15),     // F42 sabit 15 karakter
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
                   F42_MerchantId, F43_Description, F39_ResponseCode
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
        Assert.Equal(("125959", "20260315", MerchantId, "PAYMENT SYSTEM POS SIMULATOR", "00"),
            (stored.F12_TransactionTime, stored.F13_TransactionDate, stored.F42_MerchantId, stored.F43_Description, stored.F39_ResponseCode));

        await using var connection = Db.Create();
        Assert.Equal(150m, await connection.ExecuteScalarAsync<decimal>("SELECT LastTransactionAmount FROM dbo.Card WHERE CardNumber = @Pan", new { Pan }));
    }

    // ---------- İşyeri (F42) ----------

    [Theory]
    [InlineData(null)]
    [InlineData("               ")]     // 15 boşluk
    public async Task Sale_WithoutMerchantId_IsFormatErrorWithoutDebit(string? merchantId)
    {
        await SeedAsync();
        var request = Sale();
        request.MerchantId = merchantId;

        Assert.Equal("30", await AuthorizeAsync(request));

        Assert.Equal(InternalResponseCodes.MerchantIdMissing, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Sale_AtUnknownMerchant_IsDeclined03AndRecorded()
    {
        await SeedAsync();
        var request = Sale();
        request.MerchantId = "UNKNOWN".PadRight(15);

        Assert.Equal("03", await AuthorizeAsync(request));

        Assert.Equal(InternalResponseCodes.MerchantNotFound, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Equal(("UNKNOWN", "03"), (stored.F42_MerchantId, stored.F39_ResponseCode));     // reddedilen işlem de işyeriyle kaydedilir
    }

    [Fact]
    public async Task Sale_AtBlockedMerchant_IsDeclined03()
    {
        await SeedAsync(merchantStatus: "Blocked");

        Assert.Equal("03", await AuthorizeAsync(Sale()));

        Assert.Equal(InternalResponseCodes.MerchantNotActive, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Sale_WithMerchantTypeOtherThanMerchants_IsDeclined03()
    {
        await SeedAsync();
        var request = Sale();
        request.MerchantType = "5411";

        Assert.Equal("03", await AuthorizeAsync(request));

        Assert.Equal(InternalResponseCodes.MerchantTypeMismatch, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Sale_WithoutMerchantType_SkipsMccCheck()
    {
        await SeedAsync();
        var request = Sale();
        request.MerchantType = null;

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task Sale_WithUnpaddedMerchantId_IsApproved()
    {
        await SeedAsync();
        var request = Sale();
        request.MerchantId = MerchantId;

        Assert.Equal("00", await AuthorizeAsync(request));
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
        Assert.Single(Events.Events);       // tekrar gelen istek ikinci olay üretmez
    }

    [Fact]
    public async Task ApprovedSale_PublishesEventWithAccountEmailAndMaskedPan()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));

        var published = Assert.Single(Events.Events);
        Assert.Equal(await StoredIdAsync(), published.TransactionId);
        Assert.Equal(("0200", "Sale", "00", "0000"), (published.Mti, published.TransactionType, published.ResponseCode, published.Irc));
        Assert.Equal(("411111********11", AccountNo, Email), (published.MaskedCardNumber, published.AccountNo, published.Email));
        Assert.Equal((150m, "949", (decimal?)150m), (published.Amount, published.CurrencyCode, published.LocalAmount));
        Assert.Equal(("PAYMENT SYSTEM POS SIMULATOR", "20260315", "125959", "TERM0001", "000001"),
            (published.CardAcceptor, published.TransactionDate, published.LocalTime, published.TerminalId, published.Stan));
        Assert.Equal(Clock.Now, published.OccurredAt);
    }

    [Fact]
    public async Task DeclinedSale_PublishesEventWithItsCode()
    {
        await SeedAsync(balance: 100m, email: null);

        Assert.Equal("51", await AuthorizeAsync(Sale(15000)));

        var published = Assert.Single(Events.Events);
        Assert.Equal(("51", "3002", AccountNo, (string?)null), (published.ResponseCode, published.Irc, published.AccountNo, published.Email));
    }

    [Fact]
    public async Task UnknownCard_PublishesEventWithoutAccount()
    {
        await SeedAsync();
        var request = Sale();
        request.CardNumber = "5555555555554444";

        Assert.Equal("14", await AuthorizeAsync(request));

        var published = Assert.Single(Events.Events);
        Assert.Equal(("14", (long?)null, (string?)null), (published.ResponseCode, published.AccountNo, published.Email));
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
        var published = Assert.Single(Events.Events);
        Assert.Equal((10m, "840", (decimal?)492.15m), (published.Amount, published.CurrencyCode, published.LocalAmount));
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
        request.LocalDate = "20260316";

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

    // SeedAsync'in kartının CVV2'si (CardSecret'a hash'i yazılır) ve yanlış bir CVV2.
    private const string CardCvv = "123";
    private const string WrongCvv = "124";

    [Theory]
    [InlineData("812")]     // e-ticaret
    [InlineData("011")]     // elle giriş
    public async Task CardNotPresentSale_WithCorrectCvv_IsApproved(string entryMode)
    {
        await SeedAsync();
        var request = Sale(15000);
        request.EntryMode = entryMode;
        request.Cvv = CardCvv;

        Assert.Equal("00", await AuthorizeAsync(request));
        Assert.Equal(850m, await BalanceAsync());
    }

    [Theory]
    [InlineData("812")]
    [InlineData("011")]
    public async Task CardNotPresentSale_WithoutCvv_IsDeclinedN7(string entryMode)
    {
        await SeedAsync();
        var request = Sale(15000);
        request.EntryMode = entryMode;

        Assert.Equal("N7", await AuthorizeAsync(request));
        Assert.Equal("2006", LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal("N7", Assert.Single(await TransactionsAsync()).F39_ResponseCode);
    }

    [Fact]
    public async Task CardNotPresentSale_WithWrongCvv_IsDeclinedN7AndCvvIsNotLogged()
    {
        await SeedAsync();
        var logger = new ListLogger<TransactionHandler>();
        var handler = new TransactionHandler(new TransactionRepository(Db), Parameters, Rates, TestSecrets.Hasher, Events, Clock, logger);
        var request = Sale(15000);
        request.EntryMode = "812";
        request.Cvv = WrongCvv;

        var ex = await Assert.ThrowsAsync<TransactionException>(() => handler.ProcessAsync(request));

        Assert.Equal(("N7", "2006", "CVV2 missing or does not match"), (ex.ResponseCode, ex.Irc, ex.Description));
        Assert.Equal(1000m, await BalanceAsync());
        // Red sebebi loglanır, CVV'nin kendisi (gelen ya da beklenen) loglanmaz.
        Assert.Contains("DecideAsync: declined F11 (STAN)=000001 F2 (Kart No)=411111********11 - CVV2 does not match -> IRC 2006", logger.Messages);
    }

    [Fact]
    public async Task ChipSale_WithoutCvv_IsApprovedButWrongCvvIsStillChecked()
    {
        await SeedAsync();
        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));      // 051 çip: CVV istenmez

        var request = Sale(15000, localTime: "130000");
        request.Cvv = WrongCvv;                                       // yine de geldiyse doğrulanır
        Assert.Equal("N7", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task CardNotPresentSale_OnCardWithoutSecret_IsDeclinedN7()
    {
        await SeedAsync(withSecret: false);                         // CardSecret tablosundan önce açılmış kart
        var request = Sale(15000);
        request.EntryMode = "812";
        request.Cvv = CardCvv;

        Assert.Equal("N7", await AuthorizeAsync(request));          // karşılaştırılacak hash yok: eşleşmemiş sayılır
    }

    [Fact]
    public async Task CardNotPresentSale_OnCardWithoutExpiry_IsCheckedAgainstStoredCvv()
    {
        await SeedAsync(expiry: null);
        var request = Sale(15000);
        request.EntryMode = "812";
        request.Cvv = CardCvv;

        Assert.Equal("00", await AuthorizeAsync(request));          // CVV artık son kullanma tarihine bağlı değil
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
        var handler = new TransactionHandler(new TransactionRepository(unreachable), TestParameters.Create(unreachable), Rates, TestSecrets.Hasher, Events, Clock,
            NullLogger<TransactionHandler>.Instance);

        var ex = await Assert.ThrowsAsync<TransactionException>(() => handler.ProcessAsync(Sale()));
        Assert.Empty(Events.Events);        // commit yok, olay yok

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
    public async Task TransactionDate_IsStoredAsSentByGate()
    {
        await SeedAsync(expiry: "1228");
        Clock.Now = new DateTimeOffset(2027, 1, 1, 0, 0, 5, TimeSpan.Zero);
        var request = Sale();
        request.LocalDate = "20261231";     // POS'ta hâlâ yılbaşı öncesi: yıl tahmin edilmez, gelen tarih yazılır

        await AuthorizeAsync(request);

        Assert.Equal("20261231", Assert.Single(await TransactionsAsync()).F13_TransactionDate);
    }

    // ---------- Karttan karta para transferi (F3 400000, alıcı kart F48) ----------

    private const string RecipientPan = "5406680000000002";
    private const long RecipientAccountNo = 1000000002;

    private async Task SeedRecipientAsync(long accountNo = RecipientAccountNo, string status = "Active", decimal balance = 200m,
        bool withAccount = true, string? email = null)
    {
        await using var connection = Db.Create();
        if (withAccount && accountNo != AccountNo)
            await connection.ExecuteAsync(
                "INSERT INTO dbo.BankAccount (AccountNo, AccountStatus, Balance, Email) VALUES (@accountNo, @status, @balance, @email)",
                new { accountNo, status, balance, email });
        await connection.ExecuteAsync(
            "INSERT INTO dbo.Card (CardNumber, AccountNo, ExpiryDate, ContactlessAllowed) VALUES (@RecipientPan, @accountNo, '1228', 1)",
            new { RecipientPan, accountNo });
    }

    private static TransactionRequest Transfer(long amountMinor = 15000, string? recipient = RecipientPan, string localTime = "143025")
    {
        var request = Sale(amountMinor, localTime);
        request.ProcessingCode = "400000";
        request.RecipientCardNumber = recipient;
        return request;
    }

    private async Task<decimal> BalanceOfAsync(long accountNo)
    {
        await using var connection = Db.Create();
        return await connection.ExecuteScalarAsync<decimal>("SELECT Balance FROM dbo.BankAccount WHERE AccountNo = @accountNo", new { accountNo });
    }

    private async Task<string?> StoredRecipientAsync()
    {
        await using var connection = Db.Create();
        return await connection.ExecuteScalarAsync<string?>("SELECT F48_RecipientCardNo FROM dbo.DebitTransaction");
    }

    [Fact]
    public async Task Transfer_MovesMoneyBetweenAccountsAndRecordsRecipient()
    {
        await SeedAsync();
        await SeedRecipientAsync();

        Assert.Equal("00", await AuthorizeAsync(Transfer(15000)));

        Assert.Equal((850m, 350m), (await BalanceOfAsync(AccountNo), await BalanceOfAsync(RecipientAccountNo)));
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Equal(("11", "11", 150m, "00"), (stored.Otc, stored.Ots, stored.F4_Amount, stored.F39_ResponseCode));
        Assert.Equal(RecipientPan, await StoredRecipientAsync());
        var published = Assert.Single(Events.Events);
        Assert.Equal(("MoneyTransfer", "00"), (published.TransactionType, published.ResponseCode));

        await using var connection = Db.Create();
        Assert.Equal(150m, await connection.ExecuteScalarAsync<decimal>("SELECT LastTransactionAmount FROM dbo.Card WHERE CardNumber = @Pan", new { Pan }));
        Assert.Null(await connection.ExecuteScalarAsync<decimal?>("SELECT LastTransactionAmount FROM dbo.Card WHERE CardNumber = @RecipientPan", new { RecipientPan }));
    }

    [Fact]
    public async Task Transfer_PublishesEventWithMaskedRecipientAndItsAccountEmail()
    {
        await SeedAsync();
        await SeedRecipientAsync(email: "alici@example.com");

        Assert.Equal("00", await AuthorizeAsync(Transfer()));

        var published = Assert.Single(Events.Events);
        Assert.Equal(("411111********11", AccountNo, Email), (published.MaskedCardNumber, published.AccountNo, published.Email));
        Assert.Equal(("540668********02", RecipientAccountNo, "alici@example.com"),
            (published.RecipientMaskedCardNumber, published.RecipientAccountNo, published.RecipientEmail));
    }

    [Fact]
    public async Task Sale_PublishesEventWithoutRecipient()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(Sale()));

        var published = Assert.Single(Events.Events);
        Assert.Equal(((string?)null, (long?)null, (string?)null),
            (published.RecipientMaskedCardNumber, published.RecipientAccountNo, published.RecipientEmail));
    }

    [Fact]
    public async Task Transfer_Duplicate_MovesMoneyOnce()
    {
        await SeedAsync();
        await SeedRecipientAsync();

        Assert.Equal("00", await AuthorizeAsync(Transfer()));
        Assert.Equal("00", await AuthorizeAsync(Transfer()));

        Assert.Equal((850m, 350m), (await BalanceOfAsync(AccountNo), await BalanceOfAsync(RecipientAccountNo)));
    }

    [Fact]
    public async Task Transfer_OverBalance_IsDeclinedWithoutMovingMoney()
    {
        await SeedAsync(balance: 100m);
        await SeedRecipientAsync();

        Assert.Equal("51", await AuthorizeAsync(Transfer(15000)));

        Assert.Equal((100m, 200m), (await BalanceOfAsync(AccountNo), await BalanceOfAsync(RecipientAccountNo)));
    }

    [Fact]
    public async Task Transfer_InForeignCurrency_MovesLiraEquivalent()
    {
        await SeedAsync();
        await SeedRecipientAsync();
        StoreRates(("USD", 49.2152m));
        var request = Transfer(1000);       // 10,00 USD -> 492,15 TL
        request.CurrencyCode = "840";

        Assert.Equal("00", await AuthorizeAsync(request));

        Assert.Equal((507.85m, 692.15m), (await BalanceOfAsync(AccountNo), await BalanceOfAsync(RecipientAccountNo)));
        Assert.Equal(492.15m, Assert.Single(await TransactionsAsync()).LocalTxnAmount);
    }

    [Fact]
    public async Task Transfer_InForeignCurrencyWithoutRate_IsDeclined91()
    {
        await SeedAsync();
        await SeedRecipientAsync();
        var request = Transfer(1000);
        request.CurrencyCode = "840";

        Assert.Equal("91", await AuthorizeAsync(request));
        Assert.Equal((1000m, 200m), (await BalanceOfAsync(AccountNo), await BalanceOfAsync(RecipientAccountNo)));
    }

    [Fact]
    public async Task Transfer_InCurrencyWithoutTcmbRate_IsNotSupported()
    {
        await SeedAsync();
        await SeedRecipientAsync();
        var request = Transfer();
        request.CurrencyCode = "999";

        Assert.Equal("12", await AuthorizeAsync(request));
        Assert.Equal(InternalResponseCodes.CurrencyNotSupported, LastFailure!.Irc);
    }

    [Fact]
    public async Task Transfer_ZeroAmount_IsInvalidAmount()
    {
        await SeedAsync();
        await SeedRecipientAsync();

        Assert.Equal("13", await AuthorizeAsync(Transfer(0)));
    }

    [Fact]
    public async Task Transfer_ContactlessLimitDoesNotApply()
    {
        await SeedAsync(contactlessLimit: 100m);
        await SeedRecipientAsync();
        var request = Transfer(15000);
        request.EntryMode = "071";

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5406-6800")]
    [InlineData("54066800000000020000")]    // 20 hane: kart numarası olamaz
    public async Task Transfer_WithoutValidRecipientCard_IsFormatError(string? recipient)
    {
        await SeedAsync();

        Assert.Equal("30", await AuthorizeAsync(Transfer(recipient: recipient)));

        Assert.Equal(InternalResponseCodes.RecipientCardInvalid, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Transfer_ToUnknownCard_IsDeclined14()
    {
        await SeedAsync();

        Assert.Equal("14", await AuthorizeAsync(Transfer()));

        Assert.Equal(InternalResponseCodes.RecipientCardNotFound, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal(RecipientPan, await StoredRecipientAsync());     // reddedilen transfer de alıcısıyla kaydedilir
    }

    [Fact]
    public async Task Transfer_ToSameCard_IsDeclined()
    {
        await SeedAsync();

        Assert.Equal("12", await AuthorizeAsync(Transfer(recipient: Pan)));
        Assert.Equal(InternalResponseCodes.SameAccountTransfer, LastFailure!.Irc);
    }

    [Fact]
    public async Task Transfer_ToAnotherCardOfSameAccount_IsDeclined()
    {
        await SeedAsync();
        await SeedRecipientAsync(accountNo: AccountNo);

        Assert.Equal("12", await AuthorizeAsync(Transfer()));

        Assert.Equal(InternalResponseCodes.SameAccountTransfer, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Theory]
    [InlineData("Blocked", true)]
    [InlineData("Active", false)]       // alıcı kartın hesabı hiç yok
    public async Task Transfer_ToInactiveOrMissingAccount_IsDeclined62(string status, bool withAccount)
    {
        await SeedAsync();
        await SeedRecipientAsync(status: status, withAccount: withAccount);

        Assert.Equal("62", await AuthorizeAsync(Transfer()));

        Assert.Equal(InternalResponseCodes.RecipientAccountNotActive, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Transfer_FromInactiveAccount_IsDeclinedBeforeRecipientIsChecked()
    {
        await SeedAsync(status: "Blocked");

        Assert.Equal("62", await AuthorizeAsync(Transfer()));
        Assert.Equal(InternalResponseCodes.AccountNotActive, LastFailure!.Irc);
    }

    [Fact]
    public async Task Transfer_CardNotPresent_RequiresCvv()
    {
        await SeedAsync();
        await SeedRecipientAsync();
        var request = Transfer();
        request.EntryMode = "812";

        Assert.Equal("N7", await AuthorizeAsync(request));

        request.Cvv = CardCvv;
        request.LocalTime = "143026";
        Assert.Equal("00", await AuthorizeAsync(request));
    }

    [Fact]
    public async Task Sale_DoesNotStoreF48()
    {
        await SeedAsync();
        var request = Sale();
        request.RecipientCardNumber = RecipientPan;

        Assert.Equal("00", await AuthorizeAsync(request));
        Assert.Null(await StoredRecipientAsync());
    }

    // ---------- Kart durumu ----------

    [Theory]
    [InlineData("Blocked", null)]
    [InlineData("Cancelled", null)]
    [InlineData("Blocked", "Credit")]
    public async Task Sale_WithInactiveCard_IsDeclined62AndRecorded(string cardStatus, string? cardType)
    {
        await SeedAsync(cardStatus: cardStatus, cardType: cardType);
        await SeedCreditLimitAsync();

        Assert.Equal("62", await AuthorizeAsync(Sale(15000)));

        Assert.Equal(InternalResponseCodes.CardNotActive, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal(0m, (await CreditLimitAsync())?.UsedLimit ?? 0m);
    }

    // ---------- Kredi kartıyla satış ----------

    private async Task SeedCreditLimitAsync(decimal creditLimit = 500m, decimal usedLimit = 0m)
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            """
            IF EXISTS (SELECT 1 FROM dbo.Card WHERE CardNumber = @Pan AND CardType = 'Credit')
                INSERT INTO dbo.CreditCardLimit (CardNumber, CreditLimit, UsedLimit, CashLimit, StatementDay, DueDay)
                VALUES (@Pan, @creditLimit, @usedLimit, 0, 25, 5)
            """,
            new { Pan, creditLimit, usedLimit });
    }

    private async Task<CreditCardLimit?> CreditLimitAsync()
    {
        await using var connection = Db.Create();
        return await connection.QuerySingleOrDefaultAsync<CreditCardLimit>(
            "SELECT CardNumber, CreditLimit, UsedLimit, AvailableLimit, CashLimit, UsedCashLimit FROM dbo.CreditCardLimit WHERE CardNumber = @Pan", new { Pan });
    }

    private sealed record StoredCreditTransaction(string F2_CardNo, string Otc, string Ots, decimal F4_Amount, decimal? LocalTxnAmount,
        string? F42_MerchantId, string? F39_ResponseCode, byte InstallmentCount);

    private async Task<IReadOnlyList<StoredCreditTransaction>> CreditTransactionsAsync()
    {
        await using var connection = Db.Create();
        return (await connection.QueryAsync<StoredCreditTransaction>(
            "SELECT F2_CardNo, Otc, Ots, F4_Amount, LocalTxnAmount, F42_MerchantId, F39_ResponseCode, InstallmentCount FROM dbo.CreditTransaction")).AsList();
    }

    private static TransactionRequest CreditSale(long amountMinor = 15000, int? installments = null, string localTime = "125959")
    {
        var request = Sale(amountMinor, localTime);
        request.InstallmentCount = installments;
        return request;
    }

    [Fact]
    public async Task CreditSale_UsesCreditLimitAndLeavesAccountBalance()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 500m);

        Assert.Equal("00", await AuthorizeAsync(CreditSale(15000)));

        var limit = (await CreditLimitAsync())!;
        Assert.Equal((150m, 350m), (limit.UsedLimit, limit.AvailableLimit));
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Empty(await TransactionsAsync());        // debit tablosuna yazılmaz
        var stored = Assert.Single(await CreditTransactionsAsync());
        Assert.Equal((Pan, "10", "11", 150m, (decimal?)150m, MerchantId, "00", (byte)1),
            (stored.F2_CardNo, stored.Otc, stored.Ots, stored.F4_Amount, stored.LocalTxnAmount, stored.F42_MerchantId, stored.F39_ResponseCode, stored.InstallmentCount));

        await using var connection = Db.Create();
        Assert.Equal(150m, await connection.ExecuteScalarAsync<decimal>("SELECT LastTransactionAmount FROM dbo.Card WHERE CardNumber = @Pan", new { Pan }));
    }

    [Fact]
    public async Task CreditSale_InInstallments_UsesWholeAmountAndRecordsInstallmentCount()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 500m);

        Assert.Equal("00", await AuthorizeAsync(CreditSale(30000, installments: 6)));

        Assert.Equal(300m, (await CreditLimitAsync())!.UsedLimit);
        Assert.Equal(6, Assert.Single(await CreditTransactionsAsync()).InstallmentCount);
    }

    [Fact]
    public async Task CreditSale_WithExactAvailableLimit_IsApproved()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 500m, usedLimit: 350m);

        Assert.Equal("00", await AuthorizeAsync(CreditSale(15000)));

        Assert.Equal(0m, (await CreditLimitAsync())!.AvailableLimit);
    }

    [Fact]
    public async Task CreditSale_OverAvailableLimit_IsDeclined51AndRecordedWithoutUsingLimit()
    {
        await SeedAsync(cardType: "Credit", balance: 1_000_000m);     // hesaptaki para kredi kartını etkilemez
        await SeedCreditLimitAsync(creditLimit: 500m, usedLimit: 400m);

        Assert.Equal("51", await AuthorizeAsync(CreditSale(15000)));

        Assert.Equal(InternalResponseCodes.InsufficientFunds, LastFailure!.Irc);
        Assert.Equal(400m, (await CreditLimitAsync())!.UsedLimit);
        Assert.Equal("51", Assert.Single(await CreditTransactionsAsync()).F39_ResponseCode);
    }

    [Fact]
    public async Task CreditSale_WithoutCreditLimit_IsDeclined57()
    {
        await SeedAsync(cardType: "Credit");

        Assert.Equal("57", await AuthorizeAsync(CreditSale(15000)));

        Assert.Equal(InternalResponseCodes.CreditLimitNotDefined, LastFailure!.Irc);
        Assert.Equal("57", Assert.Single(await CreditTransactionsAsync()).F39_ResponseCode);
    }

    [Fact]
    public async Task CreditSale_OnInactiveAccount_IsRestricted()
    {
        await SeedAsync(cardType: "Credit", status: "Closed");
        await SeedCreditLimitAsync();

        Assert.Equal("62", await AuthorizeAsync(CreditSale(15000)));

        Assert.Equal(InternalResponseCodes.AccountNotActive, LastFailure!.Irc);
        Assert.Equal(0m, (await CreditLimitAsync())!.UsedLimit);
    }

    [Fact]
    public async Task CreditSale_ContactlessOverLimit_AsksForChipAndPin()
    {
        await SeedAsync(cardType: "Credit", contactlessLimit: 100m);
        await SeedCreditLimitAsync();
        var request = CreditSale(15000);
        request.EntryMode = "071";

        Assert.Equal("65", await AuthorizeAsync(request));

        Assert.Equal(0m, (await CreditLimitAsync())!.UsedLimit);
    }

    [Fact]
    public async Task CreditSale_InForeignCurrency_UsesLiraEquivalent()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 500m);
        StoreRates(("USD", 40m));
        var request = CreditSale(500);
        request.CurrencyCode = "840";

        Assert.Equal("00", await AuthorizeAsync(request));

        Assert.Equal(200m, (await CreditLimitAsync())!.UsedLimit);
    }

    [Fact]
    public async Task CreditSale_Duplicate_UsesLimitOnceAndReturnsFirstAnswer()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 500m);

        Assert.Equal("00", await AuthorizeAsync(CreditSale(15000)));
        Assert.Equal("00", await AuthorizeAsync(CreditSale(15000)));

        Assert.Equal(150m, (await CreditLimitAsync())!.UsedLimit);
        Assert.Single(await CreditTransactionsAsync());
        Assert.Single(Events.Events);
    }

    [Fact]
    public async Task CreditSale_DuplicateOfDeclined_StaysDeclined()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 100m);

        Assert.Equal("51", await AuthorizeAsync(CreditSale(15000)));
        Assert.Equal("51", await AuthorizeAsync(CreditSale(15000)));

        Assert.Equal(AccountNo, LastFailure!.AccountNo);
        Assert.Equal(0m, (await CreditLimitAsync())!.UsedLimit);
    }

    [Fact]
    public async Task ConcurrentCreditSales_CannotExceedCreditLimit()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync(creditLimit: 300m);

        var codes = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => AuthorizeAsync(CreditSale(10000, localTime: $"1259{i:D2}"))));

        Assert.Equal(3, codes.Count(c => c == "00"));
        Assert.Equal(2, codes.Count(c => c == "51"));
        Assert.Equal(0m, (await CreditLimitAsync())!.AvailableLimit);
    }

    [Fact]
    public async Task CreditSale_PublishesEventWithAccount()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync();

        Assert.Equal("00", await AuthorizeAsync(CreditSale(15000)));

        var published = Assert.Single(Events.Events);
        Assert.Equal(("Sale", "00", AccountNo, Email), (published.TransactionType, published.ResponseCode, published.AccountNo, published.Email));
    }

    [Fact]
    public async Task DebitSale_InInstallments_IsDeclined57WithoutDebit()
    {
        await SeedAsync();

        Assert.Equal("57", await AuthorizeAsync(CreditSale(15000, installments: 3)));

        Assert.Equal(InternalResponseCodes.InstallmentsNotAllowed, LastFailure!.Irc);
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task DebitSale_WithSingleInstallment_IsApproved()
    {
        await SeedAsync();

        Assert.Equal("00", await AuthorizeAsync(CreditSale(15000, installments: 1)));

        Assert.Equal(850m, await BalanceAsync());
    }

    [Fact]
    public async Task Transfer_FromCreditCard_IsNotSupported()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync();
        await SeedRecipientAsync();

        Assert.Equal("12", await AuthorizeAsync(Transfer(10000)));

        Assert.Equal(InternalResponseCodes.TransactionNotSupported, LastFailure!.Irc);
        Assert.Equal((0m, 200m), ((await CreditLimitAsync())!.UsedLimit, await BalanceOfAsync(RecipientAccountNo)));
    }

    [Fact]
    public async Task Transfer_FromCreditCardInInstallments_IsDeclined57()
    {
        await SeedAsync(cardType: "Credit");
        await SeedCreditLimitAsync();
        await SeedRecipientAsync();
        var request = Transfer(10000);
        request.InstallmentCount = 2;

        Assert.Equal("57", await AuthorizeAsync(request));

        Assert.Equal(InternalResponseCodes.InstallmentsNotAllowed, LastFailure!.Irc);
    }

    [Fact]
    public async Task BalanceInquiry_WithCreditCard_IsApproved()
    {
        await SeedAsync(cardType: "Credit");
        var request = Sale();
        request.ProcessingCode = "310000";

        Assert.Equal("00", await AuthorizeAsync(request));
    }

    // A->B ve B->A transferleri aynı anda: hesap satırları aynı sırayla kilitlendiği için deadlock (91) olmaz.
    [Fact]
    public async Task ConcurrentOppositeTransfers_AllComplete()
    {
        await SeedAsync();
        await SeedRecipientAsync(balance: 1000m);

        TransactionRequest Back(int i)
        {
            var request = Transfer(10000, recipient: Pan, localTime: $"1600{i:D2}");
            request.CardNumber = RecipientPan;
            return request;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 10).SelectMany(i =>
            new[] { Handler.ProcessAsync(Transfer(10000, localTime: $"1500{i:D2}")), Handler.ProcessAsync(Back(i)) }));

        Assert.All(results, code => Assert.Equal("00", code));
        Assert.Equal((1000m, 1000m), (await BalanceOfAsync(AccountNo), await BalanceOfAsync(RecipientAccountNo)));
    }
}
