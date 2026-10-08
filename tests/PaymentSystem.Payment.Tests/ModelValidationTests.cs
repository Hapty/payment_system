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
        Assert.True(IsValid(new BankAccount { AccountNo = "ACC1", AccountStatus = "Active" }));

    [Fact]
    public void BankAccount_AccountNoTooLong_IsInvalid() =>
        Assert.False(IsValid(new BankAccount { AccountNo = new string('1', 35), AccountStatus = "Active" }));

    [Fact]
    public void Card_Valid() =>
        Assert.True(IsValid(new Card { CardNumber = "4111111111111111", AccountNo = "ACC1", ExpiryDate = "1228" }));

    [Theory]
    [InlineData("128")]     // ExpiryDate MMYY: tam 4 karakter olmalı
    [InlineData("12280")]
    public void Card_ExpiryDateNotFourChars_IsInvalid(string expiry) =>
        Assert.False(IsValid(new Card { CardNumber = "4111", AccountNo = "ACC1", ExpiryDate = expiry }));

    [Fact]
    public void Card_CardNumberTooLong_IsInvalid() =>
        Assert.False(IsValid(new Card { CardNumber = new string('4', 20), AccountNo = "ACC1" }));

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
        TransmissionDateTime = "0315095959", Stan = "000001", LocalTime = "125959", LocalDate = "0315", TerminalId = "TERM0001"
    };

    [Fact]
    public void TransactionRequest_Valid() => Assert.True(IsValid(ValidTransaction()));

    [Theory]
    [InlineData("1315")]    // ay 13
    [InlineData("0300")]    // gün 00
    [InlineData("0332")]
    [InlineData("315")]
    public void TransactionRequest_InvalidLocalDate_IsInvalid(string localDate)
    {
        var request = ValidTransaction();
        request.LocalDate = localDate;
        Assert.False(IsValid(request));
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
