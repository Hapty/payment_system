using System.ComponentModel.DataAnnotations;
using PaymentSystem.Api.Models;

namespace PaymentSystem.Api.Tests;

// [ApiController] turns these data annotation failures into 400 responses before an action runs.
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
        Assert.True(IsValid(new Card { CardNumber = "4111111111111111", AccountNo = "ACC1", ExpiryDate = "1228", Cvv = "123" }));

    [Theory]
    [InlineData("12", null)]    // Cvv must be exactly 3 chars
    [InlineData(null, "128")]   // ExpiryDate must be MMYY
    public void Card_WrongFixedLengthField_IsInvalid(string? cvv, string? expiry) =>
        Assert.False(IsValid(new Card { CardNumber = "4111", AccountNo = "ACC1", Cvv = cvv, ExpiryDate = expiry }));

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
}
