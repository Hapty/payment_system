using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Transactions;

namespace PaymentSystem.Payment.Tests;

public class TransactionsControllerTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private const string Pan = "4111111111111111";
    private const string AccountNo = "ACC1";

    // Testler takvime bağlı olmasın diye "bugün" sabit: 15 Mart 2026.
    private FixedClock Clock { get; } = new(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));

    private TransactionsController Controller => new(new TransactionProcessor(Db, Clock, NullLogger<TransactionProcessor>.Instance));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record StoredTransaction(
        string Mti, string F2_CardNo, string Otc, string Ots, decimal F4_Amount, decimal? LocalTxnAmount,
        string F7_TransmissionDateTime, string F11_Stan, string F13_TransactionDate, string F37_Rrn,
        string F41_TerminalId, string F42_MerchantId, string F43_Description, string? F39_ResponseCode);

    // Her test DB'yi boşalttığı için seed verisini (02_seed_transaction_types.sql) ve bir hesap + kartı yeniden ekler.
    private async Task SeedAsync(decimal balance = 1000m, string status = "Active", string? expiry = "1228",
        bool contactless = true, decimal? contactlessLimit = 750m)
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES ('10', '11', 'Sale'), ('11', '11', 'MoneyTransfer'), ('40', '10', 'BalanceInquiry');
            INSERT INTO dbo.MtiProcessingCode (Mti, F3_ProcessingCode, Otc, Ots)
                VALUES ('0200', '000000', '10', '11'), ('0200', '400000', '11', '11'), ('0200', '310000', '40', '10');
            INSERT INTO dbo.BankAccount (AccountNo, AccountStatus, Balance) VALUES (@AccountNo, @status, @balance);
            INSERT INTO dbo.Card (CardNumber, AccountNo, ExpiryDate, ContactlessAllowed, ContactlessLimit)
                VALUES (@Pan, @AccountNo, @expiry, @contactless, @contactlessLimit);
            """,
            new { AccountNo, Pan, status, balance, expiry, contactless, contactlessLimit });
    }

    private static TransactionRequest Sale(long amountMinor = 15000, string stan = "000001") => new()
    {
        Mti = "0200",
        CardNumber = Pan,
        ProcessingCode = "000000",
        AmountMinor = amountMinor,
        CurrencyCode = "949",
        TransmissionDateTime = "0315095959",
        Stan = stan,
        LocalTime = "125959",
        LocalDate = "0315",
        TerminalId = "TERM0001",
        CardExpiry = "2812",
        MerchantType = "5999",
        EntryMode = "051",
        Rrn = "260315000001",
        MerchantId = "MERCHANT0000001",
        CardAcceptor = "PAYMENT SYSTEM POS SIMULATOR".PadRight(40)
    };

    private async Task<string> AuthorizeAsync(TransactionRequest request) => (await Controller.Authorize(request)).ResponseCode;

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
            SELECT Mti, F2_CardNo, Otc, Ots, F4_Amount, LocalTxnAmount, F7_TransmissionDateTime, F11_Stan, F13_TransactionDate,
                   F37_Rrn, F41_TerminalId, F42_MerchantId, F43_Description, F39_ResponseCode
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
        Assert.Equal(("0315095959", "000001", "20260315", "260315000001"),
            (stored.F7_TransmissionDateTime, stored.F11_Stan, stored.F13_TransactionDate, stored.F37_Rrn));
        Assert.Equal(("TERM0001", "MERCHANT0000001", "PAYMENT SYSTEM POS SIMULATOR", "00"),
            (stored.F41_TerminalId, stored.F42_MerchantId, stored.F43_Description, stored.F39_ResponseCode));

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

        // Farklı STAN'ler: hepsi ayrı işlem. 100'lük 5 satıştan sadece 3'ü bakiyeye sığar.
        var codes = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => AuthorizeAsync(Sale(10000, stan: $"{i:D6}"))));

        Assert.Equal(3, codes.Count(c => c == "00"));
        Assert.Equal(2, codes.Count(c => c == "51"));
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task SameStanFromAnotherTerminal_IsSeparateTransaction()
    {
        await SeedAsync();
        var other = Sale(15000);
        other.TerminalId = "TERM0002";

        Assert.Equal("00", await AuthorizeAsync(Sale(15000)));
        Assert.Equal("00", await AuthorizeAsync(other));

        Assert.Equal(700m, await BalanceAsync());
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
    public async Task ForeignCurrencySale_IsNotSupported()
    {
        await SeedAsync();
        var request = Sale();
        request.CurrencyCode = "840";

        Assert.Equal("12", await AuthorizeAsync(request));
        var stored = Assert.Single(await TransactionsAsync());
        Assert.Null(stored.LocalTxnAmount);
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
