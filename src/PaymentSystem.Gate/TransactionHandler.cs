using System.Diagnostics;
using PaymentSystem.Gate.Payment;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate;

// Gate'in çekirdeği, HTTP'den bağımsız: alanları doğrulanmış bir ISO8583 istek mesajı girer, cevap mesajı çıkar.
public sealed class TransactionHandler(IPaymentClient paymentClient, ILogger<TransactionHandler> logger)
{
    public const string FinancialRequestMti = "0200";

    public static IReadOnlyList<int> RequiredFinancialFields { get; } = [2, 3, 4, 7, 11, 12, 13, 41, 49];

    // Cevapta geri gönderilen istek alanları: istemci cevabı kendi isteğiyle bunlarla eşleştirir.
    private static readonly int[] EchoedFinancialFields = [2, 3, 4, 7, 11, 12, 13, 37, 41, 42, 49];

    // ISO mesajının dışında taşınan, olduğu gibi Payment'a iletilen iki değer:
    // cvv: CVV2 (ISO8583:1987'de alanı yok), loglanmaz. transactionDate: F13'ün yıllı hali (yyyymmdd); mesajdaki F13 sadece MMDD.
    public async Task<IsoMessage> ProcessAsync(IsoMessage request, string? cvv, string? transactionDate, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (request.Mti != FinancialRequestMti)
            logger.LogWarning("{Method}: unsupported MTI {Mti} {Stan} - responding 12", nameof(ProcessAsync), request.Mti, IsoLog.Field(11, request[11]));
        var response = request.Mti == FinancialRequestMti
            ? await AuthorizeAsync(request, cvv, transactionDate, cancellationToken)
            : new IsoMessage(request.ToResponseMti()).Set(39, ResponseCodes.InvalidTransaction);

        logger.LogInformation("{Method}: {RequestMti} -> {ResponseMti} {Stan} {Pan} {ResponseCode} in {ElapsedMs} ms",
            nameof(ProcessAsync), request.Mti, response.Mti, IsoLog.Field(11, request[11]), IsoLog.Field(2, request[2]),
            IsoLog.Field(39, response[39]), stopwatch.ElapsedMilliseconds);
        return response;
    }

    private async Task<IsoMessage> AuthorizeAsync(IsoMessage request, string? cvv, string? transactionDate, CancellationToken cancellationToken)
    {
        // F13'ün yılı da gerekir: Payment tarihi yyyymmdd olarak saklar.
        var missing = RequiredFinancialFields.Where(f => !request.Has(f) || (f == 13 && transactionDate is null)).ToArray();
        if (missing.Length > 0)
        {
            logger.LogWarning("{Method}: 0200 {Stan} is missing required fields {Fields} - responding 30",
                nameof(AuthorizeAsync), IsoLog.Field(11, request[11]), string.Join(", ", missing.Select(IsoLog.Label)));
            return Echo(request).Set(39, ResponseCodes.FormatError);
        }

        // Payment'a giden alanların tamamı; F13 yıllı haliyle (Payment'a giden değer), CVV sadece geldiyse "***".
        logger.LogInformation("{Method}: forwarding to Payment MTI {Mti} | {Fields}{Cvv}",
            nameof(AuthorizeAsync), request.Mti,
            IsoLog.Fields(request.Fields.Select(f => KeyValuePair.Create(f.Key, (string?)(f.Key == 13 ? transactionDate : f.Value)))),
            cvv is null ? "" : " cvv=***");
        var result = await paymentClient.AuthorizeAsync(ToPaymentRequest(request, cvv, transactionDate!), cancellationToken);
        logger.LogInformation("{Method}: Payment answered {Stan} {ResponseCode}", nameof(AuthorizeAsync),
            IsoLog.Field(11, request[11]), IsoLog.Field(39, result.ResponseCode));
        return Echo(request).Set(39, result.ResponseCode);
    }

    private static IsoMessage Echo(IsoMessage request)
    {
        var response = new IsoMessage(request.ToResponseMti());
        foreach (var field in EchoedFinancialFields)
        {
            if (request[field] is { } value)
                response.Set(field, value);
        }
        return response;
    }

    // Sadece zorunlu alanlar kontrol edildikten sonra çağrılır; bu yüzden aşağıdaki ! işaretleri güvenlidir.
    private static PaymentRequest ToPaymentRequest(IsoMessage m, string? cvv, string transactionDate) => new(
        Mti: m.Mti,
        CardNumber: m[2]!,
        ProcessingCode: m[3]!,
        AmountMinor: long.Parse(m[4]!),
        CurrencyCode: m[49]!,
        TransmissionDateTime: m[7]!,
        Stan: m[11]!,
        LocalTime: m[12]!,
        LocalDate: transactionDate,
        TerminalId: m[41]!,
        CardExpiry: m[14],
        MerchantType: m[18],
        EntryMode: m[22],
        Rrn: m[37],
        MerchantId: m[42],
        CardAcceptor: m[43],
        RecipientCardNumber: m[48],
        Cvv: cvv);
}
