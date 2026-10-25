using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

// [ApiController] bu kurallara uymayan istekleri action çalışmadan 400 ile reddeder.
public class ModelValidationTests
{
    private static bool IsValid(object model) =>
        Validator.TryValidateObject(model, new ValidationContext(model), [], validateAllProperties: true);

    private static BankAccount ValidAccount() => new() { AccountNo = 1000000001, AccountStatus = "Active", Email = "musteri@example.com" };

    private static Card ValidCard() => new()
    {
        CardNumber = "4111111111111111", AccountNo = 1000000001, CardType = "Debit", ExpiryDate = "1228",
        ContactlessAllowed = true, ContactlessLimit = 750
    };

    [Fact]
    public void BankAccount_Valid() => Assert.True(IsValid(ValidAccount()));

    [Theory]
    [InlineData(0L)]                // body'de yok
    [InlineData(999999999L)]        // 9 hane (ya da 0 ile başlayan 10 hane)
    [InlineData(10000000000L)]      // 11 hane
    [InlineData(-1000000001L)]
    public void BankAccount_AccountNoNotTenDigits_IsInvalid(long accountNo)
    {
        var account = ValidAccount();
        account.AccountNo = accountNo;
        Assert.False(IsValid(account));
    }

    [Theory]
    [InlineData(999999999L)]
    [InlineData(10000000000L)]
    public void Card_AccountNoNotTenDigits_IsInvalid(long accountNo)
    {
        var card = ValidCard();
        card.AccountNo = accountNo;
        Assert.False(IsValid(card));
    }

    [Theory]
    [InlineData(null)]              // boş kolonlu hesap açılmaz
    [InlineData("not-an-email")]
    public void BankAccount_MissingOrInvalidEmail_IsInvalid(string? email)
    {
        var account = ValidAccount();
        account.Email = email;
        Assert.False(IsValid(account));
    }

    // Değer tipli alanlar body'de yoksa sessizce 0 / false yazılmaz: JSON okunurken reddedilir ([ApiController] 400 döner).
    [Fact]
    public void BankAccount_WithoutBalance_IsRejectedWhenRead()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BankAccount>(
            """{ "accountNo": 1000000001, "accountStatus": "Active", "email": "musteri@example.com" }""", JsonSerializerOptions.Web));
        Assert.Equal(0, JsonSerializer.Deserialize<BankAccount>(
            """{ "accountNo": 1000000001, "accountStatus": "Active", "balance": 0, "email": "musteri@example.com" }""",
            JsonSerializerOptions.Web)!.Balance);
    }

    [Fact]
    public void Card_WithoutContactlessAllowed_IsRejectedWhenRead() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Card>(
            """{ "cardNumber": "4111111111111111", "accountNo": 1000000001, "cardType": "Debit", "expiryDate": "1228" }""",
            JsonSerializerOptions.Web));

    [Fact]
    public void Merchant_Valid() =>
        Assert.True(IsValid(new Merchant { MerchantId = "TRENDYOL", Name = "TRENDYOL", MerchantType = "5399", Status = "Active" }));

    [Theory]
    [InlineData("539")]     // MCC tam 4 rakam
    [InlineData("53990")]
    [InlineData("53A9")]
    public void Merchant_MerchantTypeNotFourDigits_IsInvalid(string mcc) =>
        Assert.False(IsValid(new Merchant { MerchantId = "TRENDYOL", Name = "TRENDYOL", MerchantType = mcc, Status = "Active" }));

    [Fact]
    public void Merchant_MerchantIdTooLong_IsInvalid() =>
        Assert.False(IsValid(new Merchant { MerchantId = new string('M', 16), Name = "X", MerchantType = "5399", Status = "Active" }));

    [Fact]
    public void Card_Valid() => Assert.True(IsValid(ValidCard()));

    [Fact]
    public void Card_WithoutCardTypeOrExpiry_IsInvalid()
    {
        var withoutType = ValidCard();
        withoutType.CardType = null;
        var withoutExpiry = ValidCard();
        withoutExpiry.ExpiryDate = null;
        Assert.False(IsValid(withoutType));
        Assert.False(IsValid(withoutExpiry));
    }

    // Limit temassıza açık kartta zorunlu, kapalı kartta anlamsız olduğu için boş olmalı.
    [Theory]
    [InlineData(true, 750.0, true)]
    [InlineData(true, null, false)]
    [InlineData(false, null, true)]
    [InlineData(false, 750.0, false)]
    public void Card_ContactlessLimit_FollowsContactlessAllowed(bool allowed, double? limit, bool valid)
    {
        var card = ValidCard();
        card.ContactlessAllowed = allowed;
        card.ContactlessLimit = (decimal?)limit;
        Assert.Equal(valid, IsValid(card));
    }

    [Theory]
    [InlineData("128")]     // ExpiryDate MMYY: tam 4 karakter olmalı
    [InlineData("12280")]
    public void Card_ExpiryDateNotFourChars_IsInvalid(string expiry)
    {
        var card = ValidCard();
        card.ExpiryDate = expiry;
        Assert.False(IsValid(card));
    }

    [Fact]
    public void Card_CardNumberTooLong_IsInvalid()
    {
        var card = ValidCard();
        card.CardNumber = new string('4', 20);
        Assert.False(IsValid(card));
    }

    [Fact]
    public void TransactionType_OtcNotTwoChars_IsInvalid() =>
        Assert.False(IsValid(new TransactionType { Otc = "1", Ots = "11", Name = "Sale" }));

    [Fact]
    public void MtiProcessingCode_Valid() =>
        Assert.True(IsValid(new MtiProcessingCode { Mti = "0200", F3_ProcessingCode = "000000", Otc = "10", Ots = "11" }));

    [Theory]
    [InlineData("200", "000000")]
    [InlineData("0200", "0000")]
    public void MtiProcessingCode_WrongLength_IsInvalid(string mti, string f3) =>
        Assert.False(IsValid(new MtiProcessingCode { Mti = mti, F3_ProcessingCode = f3, Otc = "10", Ots = "11" }));

    private static TransactionRequest ValidTransaction() => new()
    {
        Mti = "0200", CardNumber = "4111111111111111", ProcessingCode = "000000", AmountMinor = 15000, CurrencyCode = "949",
        TransmissionDateTime = "0315095959", Stan = "000001", LocalTime = "125959", LocalDate = "20260315", TerminalId = "TERM0001"
    };

    [Fact]
    public void TransactionRequest_Valid() => Assert.True(IsValid(ValidTransaction()));

    [Theory]
    [InlineData("20261315")]    // ay 13
    [InlineData("20260300")]    // gün 00
    [InlineData("20260332")]
    [InlineData("0315")]        // yılsız MMDD
    [InlineData("2026315")]
    public void TransactionRequest_InvalidLocalDate_IsInvalid(string localDate)
    {
        var request = ValidTransaction();
        request.LocalDate = localDate;
        Assert.False(IsValid(request));
    }

    [Theory]
    [InlineData("123", true)]
    [InlineData("1234", true)]      // Amex CID 4 hanelidir
    [InlineData("12", false)]
    [InlineData("12a", false)]
    public void TransactionRequest_Cvv_MustBeThreeOrFourDigits(string cvv, bool valid)
    {
        var request = ValidTransaction();
        request.Cvv = cvv;
        Assert.Equal(valid, IsValid(request));
    }

    [Fact]
    public void TransactionRequest_NegativeAmount_IsInvalid()
    {
        var request = ValidTransaction();
        request.AmountMinor = -1;
        Assert.False(IsValid(request));
    }

    [Fact]
    public void TransactionRequest_TerminalIdTooLong_IsInvalid()
    {
        var request = ValidTransaction();
        request.TerminalId = "TERM00011";
        Assert.False(IsValid(request));
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Blocked", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Closed", false)]
    [InlineData("", false)]
    public void Card_Status_MustBeKnown(string status, bool valid)
    {
        var card = ValidCard();
        card.Status = status;
        Assert.Equal(valid, IsValid(card));
    }

    private static CreditCardLimit ValidLimit() =>
        new() { CardNumber = "4111111111111111", CreditLimit = 50_000m, CashLimit = 10_000m, StatementDay = 25, DueDay = 5 };

    [Fact]
    public void CreditCardLimit_Valid() => Assert.True(IsValid(ValidLimit()));

    [Fact]
    public void CreditCardLimit_CashLimitOverCreditLimit_IsInvalid()
    {
        var limit = ValidLimit();
        limit.CashLimit = 50_000.01m;
        Assert.False(IsValid(limit));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(29, 5)]
    [InlineData(25, 0)]
    [InlineData(25, 31)]
    public void CreditCardLimit_DaysOutsideOneToTwentyEight_AreInvalid(byte statementDay, byte dueDay)
    {
        var limit = ValidLimit();
        (limit.StatementDay, limit.DueDay) = (statementDay, dueDay);
        Assert.False(IsValid(limit));
    }

    [Fact]
    public void CreditCardLimit_NegativeLimit_IsInvalid()
    {
        var limit = ValidLimit();
        (limit.CreditLimit, limit.CashLimit) = (-1m, 0m);
        Assert.False(IsValid(limit));
    }

    [Fact]
    public void CreditCardLimit_WithoutCreditLimit_IsRejectedWhenRead() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreditCardLimit>(
            """{"cardNumber":"4111111111111111","cashLimit":0,"statementDay":25,"dueDay":5}""", JsonSerializerOptions.Web));

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(36, true)]
    [InlineData(0, false)]
    [InlineData(37, false)]
    public void TransactionRequest_InstallmentCount_IsOneToThirtySix(int? installments, bool valid)
    {
        var request = ValidTransaction();
        request.InstallmentCount = installments;
        Assert.Equal(valid, IsValid(request));
    }
}
