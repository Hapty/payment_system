using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaymentSystem.Gate.Tests;

public class TransactionEndpointTests
{
    private FakePaymentClient _payment = new();
    private readonly ListLogger<TransactionEndpointTests> _logger = new();

    private static TransactionRequest Sale() => new(
        Mti: "0200", CardNumber: "4111111111111111", ProcessingCode: "000000", AmountMinor: 15000, CurrencyCode: "949",
        TransmissionDateTime: "1002143015", Stan: "123456", LocalTime: "173015", LocalDate: "1002", TerminalId: "TERM0001",
        CardExpiry: "2812", MerchantType: "5999", EntryMode: "051", Rrn: "261002123456", MerchantId: "MERCHANT0000001",
        CardAcceptor: null);

    private async Task<IResult> PostAsync(TransactionRequest request) =>
        (await TransactionEndpoint.HandleAsync(
            request, new TransactionHandler(_payment, NullLogger<TransactionHandler>.Instance), _logger,
            CancellationToken.None)).Result;

    private async Task<IDictionary<string, string[]>> ValidationErrorsAsync(TransactionRequest request)
    {
        var problem = Assert.IsType<ValidationProblem>(await PostAsync(request));
        Assert.Empty(_payment.Requests);
        return problem.ProblemDetails.Errors;
    }

    [Theory]
    [InlineData("00")]
    [InlineData("51")]
    public async Task ValidSale_IsForwardedAndAnsweredWithPaymentCode(string paymentCode)
    {
        _payment = new FakePaymentClient(paymentCode);

        var ok = Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale()));

        Assert.Equal(new TransactionResponse("0210", paymentCode, "123456", "261002123456"), ok.Value);
        var forwarded = Assert.Single(_payment.Requests);
        Assert.Equal(15000, forwarded.AmountMinor);
        Assert.Equal("5999", forwarded.MerchantType);
        Assert.Equal("MERCHANT0000001", forwarded.MerchantId);
    }

    [Fact]
    public async Task ValidSale_LogsRequestAndResponseWithMaskedPan()
    {
        await PostAsync(Sale());

        Assert.Contains(_logger.Messages, m => m.StartsWith("HandleAsync: request received") && m.Contains("PAN 411111********11"));
        Assert.Contains(_logger.Messages, m => m.StartsWith("HandleAsync: responding 0210"));
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("4111111111111111"));
    }

    [Fact]
    public async Task InvalidFields_LogWhichFieldsFailedWithMaskedPan()
    {
        await PostAsync(Sale() with { Stan = "12" });

        var failed = Assert.Single(_logger.Messages, m => m.StartsWith("HandleAsync: validation failed"));
        Assert.Contains("stan", failed);
        Assert.Contains("PAN 411111********11", failed);
    }

    [Fact]
    public async Task OptionalFieldsCanBeLeftOut()
    {
        var request = Sale() with { CardExpiry = null, MerchantType = null, EntryMode = null, Rrn = null, MerchantId = null };

        var ok = Assert.IsType<Ok<TransactionResponse>>(await PostAsync(request));

        Assert.Equal("00", ok.Value!.ResponseCode);
        Assert.Null(ok.Value.Rrn);
        Assert.Null(Assert.Single(_payment.Requests).CardExpiry);
    }

    [Fact]
    public async Task CardAcceptor_MustBeExactly40Characters()
    {
        Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale() with { CardAcceptor = "PAYMENT SYSTEM DEMO".PadRight(40) }));
        _payment.Requests.Clear();

        var errors = await ValidationErrorsAsync(Sale() with { CardAcceptor = "TOO SHORT" });
        Assert.Contains("F43", Assert.Single(errors["cardAcceptor"]));
    }

    [Fact]
    public async Task MalformedFields_AreAllReportedByName()
    {
        var errors = await ValidationErrorsAsync(Sale() with { CardNumber = "4111-1111", LocalDate = "12", CurrencyCode = "TRY" });

        Assert.Equal(["cardNumber", "currencyCode", "localDate"], errors.Keys.Order());
    }

    [Fact]
    public async Task MissingRequiredFields_AreReported()
    {
        var errors = await ValidationErrorsAsync(Sale() with { AmountMinor = null, Stan = null });

        Assert.Equal("F4 is required.", Assert.Single(errors["amountMinor"]));
        Assert.Equal("F11 is required.", Assert.Single(errors["stan"]));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1_000_000_000_000L)]   // 12 haneye sığmaz
    public async Task AmountOutOfRange_IsRejected(long amount) =>
        Assert.True((await ValidationErrorsAsync(Sale() with { AmountMinor = amount })).ContainsKey("amountMinor"));

    [Theory]
    [InlineData(null)]
    [InlineData("200")]
    [InlineData("02A0")]
    [InlineData("0210")]   // cevap MTI'ı, istek değil
    public async Task InvalidMti_IsRejected(string? mti) =>
        Assert.Equal(["mti"], (await ValidationErrorsAsync(Sale() with { Mti = mti })).Keys);

    [Fact]
    public async Task OtherRequestMti_IsAnsweredWithoutRequiringFinancialFields()
    {
        var request = new TransactionRequest("0400", null, null, null, null, null, "000002", null, null, null, null, null, null, null, null, null);

        var ok = Assert.IsType<Ok<TransactionResponse>>(await PostAsync(request));

        Assert.Equal(("0410", "12"), (ok.Value!.Mti, ok.Value.ResponseCode));
        Assert.Empty(_payment.Requests);
    }
}
