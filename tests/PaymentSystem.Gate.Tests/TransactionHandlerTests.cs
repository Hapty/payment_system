using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

public class TransactionHandlerTests
{
    private const string TransactionDate = "20261002";      // TestMessages.Sale()'daki F13 (1002) yılıyla
    private readonly FakePaymentClient _payment = new();

    private Task<IsoMessage> ProcessAsync(IsoMessage request) =>
        new TransactionHandler(_payment, NullLogger<TransactionHandler>.Instance).ProcessAsync(request, cvv: null, TransactionDate, CancellationToken.None);

    [Fact]
    public async Task Sale_IsForwardedToPaymentWithReadableFields()
    {
        await ProcessAsync(TestMessages.Sale());

        var forwarded = Assert.Single(_payment.Requests);
        Assert.Equal("4111111111111111", forwarded.CardNumber);
        Assert.Equal("000000", forwarded.ProcessingCode);
        Assert.Equal(15000, forwarded.AmountMinor);
        Assert.Equal("949", forwarded.CurrencyCode);
        Assert.Equal("123456", forwarded.Stan);
        Assert.Equal("TERM0001", forwarded.TerminalId);
        Assert.Equal("2812", forwarded.CardExpiry);
        Assert.Equal("20261002", forwarded.LocalDate);      // ISO mesajındaki MMDD değil, yıllı hali
        Assert.Equal("TRENDYOL                 ISTANBUL     TR", forwarded.CardAcceptor);
        Assert.Null(forwarded.RecipientCardNumber);
    }

    [Fact]
    public async Task Transfer_ForwardsF48AsRecipientCard()
    {
        await ProcessAsync(TestMessages.Sale().Set(3, "400000").Set(48, "5406680000000002"));

        var forwarded = Assert.Single(_payment.Requests);
        Assert.Equal(("400000", "5406680000000002"), (forwarded.ProcessingCode, forwarded.RecipientCardNumber));
    }

    [Fact]
    public async Task Sale_WithoutFullTransactionDate_IsFormatErrorAndNotForwarded()
    {
        var response = await new TransactionHandler(_payment, NullLogger<TransactionHandler>.Instance)
            .ProcessAsync(TestMessages.Sale(), cvv: null, transactionDate: null, CancellationToken.None);

        Assert.Equal("30", response[39]);
        Assert.Empty(_payment.Requests);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("51")]   // örn. yetersiz bakiye; kararı Payment verir
    [InlineData("91")]
    public async Task Sale_ResponseCarriesPaymentCodeAndEchoesMatchingFields(string paymentCode)
    {
        var handler = new TransactionHandler(new FakePaymentClient(paymentCode), NullLogger<TransactionHandler>.Instance);
        var request = TestMessages.Sale();

        var response = await handler.ProcessAsync(request, cvv: null, TransactionDate, CancellationToken.None);

        Assert.Equal("0210", response.Mti);
        Assert.Equal(paymentCode, response[39]);
        foreach (var field in new[] { 2, 3, 4, 7, 11, 12, 13, 37, 41, 42, 49 })
            Assert.Equal(request[field], response[field]);
        Assert.False(response.Has(14));   // son kullanma tarihi cevapta geri gönderilmez
    }

    [Fact]
    public async Task Sale_LogsEachStepWithMaskedPan()
    {
        var logger = new ListLogger<TransactionHandler>();

        await new TransactionHandler(_payment, logger).ProcessAsync(TestMessages.Sale(), cvv: null, TransactionDate, CancellationToken.None);

        // Payment'a giden alanların hepsi, numara sırasıyla ve ne olduklarıyla; F13 yıllı haliyle.
        Assert.Contains(
            "AuthorizeAsync: forwarding to Payment MTI 0200 | F2 (Kart No)=411111********11 F3 (İşlem Kodu)=000000 " +
            "F4 (Tutar, kuruş)=000000015000 F7 (Gönderim Zamanı)=1002143015 F11 (STAN)=123456 F12 (Yerel Saat)=173015 " +
            "F13 (Yerel Tarih)=20261002 F14 (Son Kullanma)=2812 F18 (İşyeri Tipi)=5399 F22 (Giriş Yöntemi)=051 " +
            "F37 (RRN)=261002123456 F41 (Terminal No)=TERM0001 F42 (İşyeri No)=TRENDYOL " +
            "F43 (İşyeri Adı)=TRENDYOL                 ISTANBUL     TR F49 (Para Birimi)=949",
            logger.Messages);
        Assert.Contains("AuthorizeAsync: Payment answered F11 (STAN)=123456 F39 (Cevap Kodu)=00", logger.Messages);
        Assert.Contains(logger.Messages, m => m.StartsWith("ProcessAsync: 0200 -> 0210 F11 (STAN)=123456 F2 (Kart No)=411111********11 F39 (Cevap Kodu)=00 in "));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("4111111111111111"));
    }

    [Fact]
    public async Task Transfer_LogsRecipientCardMaskedAndCvvHidden()
    {
        var logger = new ListLogger<TransactionHandler>();

        await new TransactionHandler(_payment, logger).ProcessAsync(
            TestMessages.Sale().Set(3, "400000").Set(48, "5406680000000002"), cvv: "987", TransactionDate, CancellationToken.None);

        var forwarding = Assert.Single(logger.Messages, m => m.StartsWith("AuthorizeAsync: forwarding to Payment"));
        Assert.Contains("F48 (Alıcı Kart No)=540668********02 F49 (Para Birimi)=949 cvv=***", forwarding);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("5406680000000002") || m.Contains("987"));
    }

    [Fact]
    public async Task InstallmentSale_LogsAndForwardsInstallmentCount()
    {
        var logger = new ListLogger<TransactionHandler>();

        await new TransactionHandler(_payment, logger).ProcessAsync(
            TestMessages.Sale(), cvv: null, TransactionDate, CancellationToken.None, installmentCount: 6);

        var forwarding = Assert.Single(logger.Messages, m => m.StartsWith("AuthorizeAsync: forwarding to Payment"));
        Assert.EndsWith("F49 (Para Birimi)=949 installmentCount=6", forwarding);
        Assert.Equal(6, Assert.Single(_payment.Requests).InstallmentCount);
    }

    [Fact]
    public async Task MissingRequiredField_IsLoggedWithItsLabel()
    {
        var logger = new ListLogger<TransactionHandler>();
        var request = new IsoMessage("0200");
        foreach (var (field, value) in TestMessages.Sale().Fields.Where(f => f.Key != 4))
            request.Set(field, value);

        await new TransactionHandler(_payment, logger).ProcessAsync(request, cvv: null, TransactionDate, CancellationToken.None);

        Assert.Contains("AuthorizeAsync: 0200 F11 (STAN)=123456 is missing required fields F4 (Tutar, kuruş) - responding 30", logger.Messages);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(42)]    // işyeri no: Payment'ta Merchant tablosunda aranır
    [InlineData(14)]    // DB kolonu olan alanlar boş gelemez
    [InlineData(18)]
    [InlineData(22)]
    [InlineData(43)]
    public async Task Sale_MissingRequiredField_IsFormatErrorAndNotForwarded(int missing)
    {
        var request = new IsoMessage("0200");
        foreach (var (field, value) in TestMessages.Sale().Fields.Where(f => f.Key != missing))
            request.Set(field, value);

        var response = await ProcessAsync(request);

        Assert.Equal("0210", response.Mti);
        Assert.Equal("30", response[39]);
        Assert.Empty(_payment.Requests);
    }

    [Theory]
    [InlineData("0400", "0410")]   // reversal: henüz desteklenmiyor
    [InlineData("0800", "0810")]   // network yönetimi: TCP girişiyle birlikte kaldırıldı
    public async Task UnsupportedMti_IsInvalidTransaction(string mti, string responseMti)
    {
        var response = await ProcessAsync(new IsoMessage(mti).Set(11, "000002"));

        Assert.Equal(responseMti, response.Mti);
        Assert.Equal("12", response[39]);
        Assert.Empty(_payment.Requests);
    }
}
