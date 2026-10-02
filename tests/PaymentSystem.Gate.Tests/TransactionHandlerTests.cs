using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

public class TransactionHandlerTests
{
    private readonly FakePaymentClient _payment = new();

    private TransactionHandler Handler() => new(_payment, NullLogger<TransactionHandler>.Instance);

    private async Task<IsoMessage> SendAsync(IsoMessage request)
    {
        var response = await Handler().HandleAsync(TestMessages.Pack(request), CancellationToken.None);
        return TestMessages.Unpack(response!);
    }

    [Fact]
    public async Task EchoTest_IsAnsweredByGateWithoutCallingPayment()
    {
        var response = await SendAsync(TestMessages.Echo());

        Assert.Equal("0810", response.Mti);
        Assert.Equal("00", response[39]);
        Assert.Equal("000001", response[11]);
        Assert.Equal("301", response[70]);
        Assert.Empty(_payment.Requests);
    }

    [Fact]
    public async Task Sale_IsForwardedToPaymentWithReadableFields()
    {
        await SendAsync(TestMessages.Sale());

        var forwarded = Assert.Single(_payment.Requests);
        Assert.Equal("4111111111111111", forwarded.CardNumber);
        Assert.Equal("000000", forwarded.ProcessingCode);
        Assert.Equal(15000, forwarded.AmountMinor);
        Assert.Equal("949", forwarded.CurrencyCode);
        Assert.Equal("123456", forwarded.Stan);
        Assert.Equal("TERM0001", forwarded.TerminalId);
        Assert.Equal("2812", forwarded.CardExpiry);
        Assert.Null(forwarded.CardAcceptor);   // F43 was not sent
    }

    [Theory]
    [InlineData("00")]
    [InlineData("51")]   // e.g. insufficient funds, decided by Payment
    [InlineData("91")]
    public async Task Sale_ResponseCarriesPaymentCodeAndEchoesMatchingFields(string paymentCode)
    {
        var payment = new FakePaymentClient(paymentCode);
        var handler = new TransactionHandler(payment, NullLogger<TransactionHandler>.Instance);
        var request = TestMessages.Sale();

        var response = TestMessages.Unpack((await handler.HandleAsync(TestMessages.Pack(request), CancellationToken.None))!);

        Assert.Equal("0210", response.Mti);
        Assert.Equal(paymentCode, response[39]);
        foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 37, 41, 42, 49 })
            Assert.Equal(request[field], response[field]);
        Assert.False(response.Has(14));   // card expiry is not echoed back
    }

    [Fact]
    public async Task Sale_MissingRequiredField_IsFormatErrorAndNotForwarded()
    {
        var request = new IsoMessage("0200");
        foreach (var (field, value) in TestMessages.Sale().Fields.Where(f => f.Key != 4))
            request.Set(field, value);

        var response = await SendAsync(request);

        Assert.Equal("0210", response.Mti);
        Assert.Equal("30", response[39]);
        Assert.Empty(_payment.Requests);
    }

    [Fact]
    public async Task UnsupportedMti_IsInvalidTransaction()
    {
        var response = await SendAsync(new IsoMessage("0400").Set(11, "000002"));

        Assert.Equal("0410", response.Mti);
        Assert.Equal("12", response[39]);
    }

    [Fact]
    public async Task MalformedBodyWithReadableMti_IsFormatError()
    {
        byte[] data = [.. TestMessages.Pack(TestMessages.Sale()), (byte)'X'];

        var response = TestMessages.Unpack((await Handler().HandleAsync(data, CancellationToken.None))!);

        Assert.Equal("0210", response.Mti);
        Assert.Equal("30", response[39]);
    }

    [Fact]
    public async Task UnreadableMessage_ReturnsNull() =>
        Assert.Null(await Handler().HandleAsync("garbage"u8.ToArray(), CancellationToken.None));

    [Fact]
    public async Task ResponseMessage_IsDropped() =>
        Assert.Null(await Handler().HandleAsync(TestMessages.Pack(new IsoMessage("0210").Set(39, "00")), CancellationToken.None));

    [Fact]
    public async Task MalformedResponseMessage_IsDropped()
    {
        byte[] data = [.. "0210"u8, 0, 0, 0];

        Assert.Null(await Handler().HandleAsync(data, CancellationToken.None));
    }
}
