using System.ComponentModel.DataAnnotations;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Tests;

// [ApiController] bu kurallara uymayan istekleri action çalışmadan 400 ile reddeder.
public class ModelValidationTests
{
    private static bool IsValid(object model) =>
        Validator.TryValidateObject(model, new ValidationContext(model), [], validateAllProperties: true);

    [Fact]
    public void BankAccount_Valid() =>
        Assert.True(IsValid(new BankAccount { AccountNo = 1000000001, AccountStatus = "Active" }));

    [Theory]
    [InlineData(0L)]                // body'de yok
    [InlineData(999999999L)]        // 9 hane (ya da 0 ile başlayan 10 hane)
    [InlineData(10000000000L)]      // 11 hane
    [InlineData(-1000000001L)]
    public void BankAccount_AccountNoNotTenDigits_IsInvalid(long accountNo) =>
        Assert.False(IsValid(new BankAccount { AccountNo = accountNo, AccountStatus = "Active" }));

    [Theory]
    [InlineData(999999999L)]
    [InlineData(10000000000L)]
    public void Card_AccountNoNotTenDigits_IsInvalid(long accountNo) =>
        Assert.False(IsValid(new Card { CardNumber = "4111111111111111", AccountNo = accountNo }));

    [Fact]
    public void BankAccount_WithEmail_IsValid() =>
        Assert.True(IsValid(new BankAccount { AccountNo = 1000000001, AccountStatus = "Active", Email = "musteri@example.com" }));

    [Fact]
    public void BankAccount_InvalidEmail_IsInvalid() =>
        Assert.False(IsValid(new BankAccount { AccountNo = 1000000001, AccountStatus = "Active", Email = "not-an-email" }));

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
    public void Card_Valid() =>
        Assert.True(IsValid(new Card { CardNumber = "4111111111111111", AccountNo = 1000000001, ExpiryDate = "1228" }));

    [Theory]
    [InlineData("128")]     // ExpiryDate MMYY: tam 4 karakter olmalı
    [InlineData("12280")]
    public void Card_ExpiryDateNotFourChars_IsInvalid(string expiry) =>
        Assert.False(IsValid(new Card { CardNumber = "4111", AccountNo = 1000000001, ExpiryDate = expiry }));

    [Fact]
    public void Card_CardNumberTooLong_IsInvalid() =>
        Assert.False(IsValid(new Card { CardNumber = new string('4', 20), AccountNo = 1000000001 }));

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
}
