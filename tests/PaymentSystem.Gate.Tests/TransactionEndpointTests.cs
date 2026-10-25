using System.Text.Json;
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
        TransmissionDateTime: "1002143015", Stan: "123456", LocalTime: "173015", LocalDate: "20261002", TerminalId: "TERM0001",
        CardExpiry: "2812", MerchantType: "5999", EntryMode: "051", Rrn: "261002123456", MerchantId: "TRENDYOL       ",
        CardAcceptor: "TRENDYOL                 ISTANBUL     TR");

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

        Assert.Equal(new TransactionResponse("0210", paymentCode, "123456"), ok.Value);
        var forwarded = Assert.Single(_payment.Requests);
        Assert.Equal(15000, forwarded.AmountMinor);
        Assert.Equal("5999", forwarded.MerchantType);
        Assert.Equal("TRENDYOL       ", forwarded.MerchantId);
    }

    [Fact]
    public async Task ValidSale_LogsRequestAndResponseWithMaskedPan()
    {
        await PostAsync(Sale());

        Assert.Contains("HandleAsync: request received MTI 0200 F11 (STAN)=123456 F41 (Terminal No)=TERM0001 F2 (Kart No)=411111********11", _logger.Messages);
        Assert.Contains("HandleAsync: responding 0210 F11 (STAN)=123456 F39 (Cevap Kodu)=00", _logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("4111111111111111"));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234")]
    public async Task Cvv_IsForwardedToPaymentButNeverLogged(string cvv)
    {
        Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale() with { EntryMode = "812", Cvv = cvv }));

        Assert.Equal(cvv, Assert.Single(_payment.Requests).Cvv);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains($"CVV {cvv}") || m.Contains($"cvv {cvv}"));
    }

    [Fact]
    public async Task Cvv_IsOptional() =>
        Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale()));

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("12a")]
    public async Task InvalidCvv_IsRejectedWith400(string cvv) =>
        Assert.Equal(["Must be 3 or 4 digits."], (await ValidationErrorsAsync(Sale() with { Cvv = cvv }))["cvv"]);

    [Fact]
    public async Task InvalidFields_LogWhichFieldsFailedWithMaskedPan()
    {
        await PostAsync(Sale() with { Stan = "12" });

        var failed = Assert.Single(_logger.Messages, m => m.StartsWith("HandleAsync: validation failed"));
        Assert.Contains("fields F11 (STAN) - responding 400", failed);
        Assert.Contains("F2 (Kart No)=411111********11", failed);
    }

    // F37'nin DB'de kolonu yok; F48 sadece transferde gelir.
    [Fact]
    public async Task OptionalFieldsCanBeLeftOut()
    {
        var request = Sale() with { Rrn = null, RecipientCardNumber = null };

        var ok = Assert.IsType<Ok<TransactionResponse>>(await PostAsync(request));

        Assert.Equal("00", ok.Value!.ResponseCode);
        Assert.Null(Assert.Single(_payment.Requests).Rrn);
    }

    // DebitTransaction'da kolonu olan alanlar boş gelemez: boş kolonlu kayıt açılmaz.
    [Fact]
    public async Task FieldsStoredInDb_AreRequired()
    {
        var errors = await ValidationErrorsAsync(Sale() with { CardExpiry = null, MerchantType = null, EntryMode = null, CardAcceptor = null });

        Assert.Equal(["F14", "F18", "F22", "F43"], errors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("F43 is required.", Assert.Single(errors["F43"]));
    }

    [Fact]
    public async Task CardAcceptor_MustBeExactly40Characters()
    {
        Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale() with { CardAcceptor = "PAYMENT SYSTEM DEMO".PadRight(40) }));
        _payment.Requests.Clear();

        var errors = await ValidationErrorsAsync(Sale() with { CardAcceptor = "TOO SHORT" });
        Assert.Contains("F43", Assert.Single(errors["F43"]));
    }

    [Fact]
    public async Task MalformedFields_AreAllReportedByFieldNumber()
    {
        var errors = await ValidationErrorsAsync(Sale() with { CardNumber = "4111-1111", LocalDate = "12", CurrencyCode = "TRY" });

        Assert.Equal(["F13", "F2", "F49"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MissingRequiredFields_AreReported()
    {
        var errors = await ValidationErrorsAsync(Sale() with { AmountMinor = null, Stan = null });

        Assert.Equal("F4 is required.", Assert.Single(errors["F4"]));
        Assert.Equal("F11 is required.", Assert.Single(errors["F11"]));
    }

    [Fact]
    public async Task MissingMerchantId_IsReported()
    {
        Assert.Equal("F42 is required.", Assert.Single((await ValidationErrorsAsync(Sale() with { MerchantId = null }))["F42"]));
        Assert.Empty(_payment.Requests);
    }

    [Fact]
    public async Task MissingLocalDate_IsReported() =>
        Assert.Equal("F13 is required.", Assert.Single((await ValidationErrorsAsync(Sale() with { LocalDate = null }))["F13"]));

    [Fact]
    public async Task LocalDate_IsForwardedWithYear()
    {
        var ok = Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale() with { LocalDate = "20261231" }));

        Assert.Equal("00", ok.Value!.ResponseCode);
        Assert.Equal("20261231", Assert.Single(_payment.Requests).LocalDate);
    }

    [Theory]
    [InlineData("1002")]        // yılsız MMDD
    [InlineData("20261302")]    // ay 13
    [InlineData("20260230")]    // 30 Şubat
    [InlineData("2026-10-02")]
    [InlineData("２０２６１００２")]  // ASCII olmayan rakamlar
    public async Task InvalidLocalDate_IsRejected(string localDate) =>
        Assert.StartsWith("F13: Must be a date in yyyymmdd format",
            Assert.Single((await ValidationErrorsAsync(Sale() with { LocalDate = localDate }))["F13"]));

    [Fact]
    public async Task Transfer_ForwardsF48AsRecipientCard()
    {
        Assert.IsType<Ok<TransactionResponse>>(await PostAsync(Sale() with { ProcessingCode = "400000", RecipientCardNumber = "5406680000000002" }));

        Assert.Equal("5406680000000002", Assert.Single(_payment.Requests).RecipientCardNumber);
    }

    [Fact]
    public async Task TooLongF48_IsRejected() =>
        Assert.True((await ValidationErrorsAsync(Sale() with { RecipientCardNumber = new string('1', 1000) })).ContainsKey("F48"));

    // JSON sözleşmesi: istek ve cevap alanları ISO8583 alan numarasıyla ("F2", "F39" ...) yazılır.
    [Fact]
    public void Json_UsesFieldNumbers()
    {
        var request = JsonSerializer.Deserialize<TransactionRequest>(
            """
            { "mti": "0200", "F2": "5406680000000001", "F3": "400000", "F4": 15000, "F7": "1002143025", "F11": "000001",
              "F12": "143025", "F13": "20261002", "F14": "2712", "F22": "051", "F41": "TERM0001", "F48": "5406680000000002",
              "F49": "949", "cvv": "123" }
            """, JsonSerializerOptions.Web)!;

        Assert.Equal(Sale() with
        {
            CardNumber = "5406680000000001", ProcessingCode = "400000", TransmissionDateTime = "1002143025", Stan = "000001",
            LocalTime = "143025", CardExpiry = "2712", MerchantType = null, Rrn = null, MerchantId = null, CardAcceptor = null,
            RecipientCardNumber = "5406680000000002", Cvv = "123"
        }, request);
        Assert.Equal("""{"mti":"0210","F39":"00","F11":"000001"}""",
            JsonSerializer.Serialize(new TransactionResponse("0210", "00", "000001"), JsonSerializerOptions.Web));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1_000_000_000_000L)]   // 12 haneye sığmaz
    public async Task AmountOutOfRange_IsRejected(long amount) =>
        Assert.True((await ValidationErrorsAsync(Sale() with { AmountMinor = amount })).ContainsKey("F4"));

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
