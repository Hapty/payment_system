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

    public async Task<IsoMessage> ProcessAsync(IsoMessage request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = request.Mti == FinancialRequestMti
            ? await AuthorizeAsync(request, cancellationToken)
            : new IsoMessage(request.ToResponseMti()).Set(39, ResponseCodes.InvalidTransaction);

        logger.LogInformation("{RequestMti} -> {ResponseMti} STAN {Stan} PAN {Pan} F39 {ResponseCode} in {ElapsedMs} ms",
            request.Mti, response.Mti, request[11], PanMask.Mask(request[2]), response[39], stopwatch.ElapsedMilliseconds);
        return response;
    }

    private async Task<IsoMessage> AuthorizeAsync(IsoMessage request, CancellationToken cancellationToken)
    {
        var missing = RequiredFinancialFields.Where(f => !request.Has(f)).ToArray();
        if (missing.Length > 0)
        {
            logger.LogWarning("0200 STAN {Stan} is missing required fields {Fields}", request[11], string.Join(", ", missing));
            return Echo(request).Set(39, ResponseCodes.FormatError);
        }

        var result = await paymentClient.AuthorizeAsync(ToPaymentRequest(request), cancellationToken);
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
    private static PaymentRequest ToPaymentRequest(IsoMessage m) => new(
        Mti: m.Mti,
        CardNumber: m[2]!,
        ProcessingCode: m[3]!,
        AmountMinor: long.Parse(m[4]!),
        CurrencyCode: m[49]!,
        TransmissionDateTime: m[7]!,
        Stan: m[11]!,
        LocalTime: m[12]!,
        LocalDate: m[13]!,
        TerminalId: m[41]!,
        CardExpiry: m[14],
        MerchantType: m[18],
        EntryMode: m[22],
        Rrn: m[37],
        MerchantId: m[42],
        CardAcceptor: m[43]);
}
