using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate;

// POST /api/transactions gövdesi: bir finansal isteğin ISO8583 alanları, okunabilir isimlerle.
public sealed record TransactionRequest(
    string? Mti,
    string? CardNumber,             // F2
    string? ProcessingCode,         // F3: 000000 satış, 310000 bakiye sorgu ...
    long? AmountMinor,              // F4, kuruş cinsinden: 15000 = 150,00
    string? CurrencyCode,           // F49, örn. 949 = TRY
    string? TransmissionDateTime,   // F7  MMDDhhmmss
    string? Stan,                   // F11
    string? LocalTime,              // F12 hhmmss
    string? LocalDate,              // F13 MMDD
    string? TerminalId,             // F41
    string? CardExpiry,             // F14 YYMM
    string? MerchantType,           // F18
    string? EntryMode,              // F22
    string? Rrn,                    // F37
    string? MerchantId,             // F42
    string? CardAcceptor);          // F43

// ResponseCode = F39. Onay da red de (örn. 51 yetersiz bakiye) 200 ile döner.
public sealed record TransactionResponse(string Mti, string ResponseCode, string? Stan, string? Rrn);

// İşlemlerin sisteme tek giriş noktası. İstek ISO8583 mesajına çevrilir ve her alan ISO8583 tanımına göre doğrulanır;
// bozuk ya da eksik alan 400 ile, hangi alanda ne sorun olduğu yazılarak döner. Geçerli istek TransactionHandler'a gider.
public static class TransactionEndpoint
{
    private static readonly (string Name, int Field, Func<TransactionRequest, string?> Value)[] Fields =
    [
        ("cardNumber", 2, r => r.CardNumber),
        ("processingCode", 3, r => r.ProcessingCode),
        ("amountMinor", 4, r => r.AmountMinor?.ToString("D12", CultureInfo.InvariantCulture)),
        ("transmissionDateTime", 7, r => r.TransmissionDateTime),
        ("stan", 11, r => r.Stan),
        ("localTime", 12, r => r.LocalTime),
        ("localDate", 13, r => r.LocalDate),
        ("cardExpiry", 14, r => r.CardExpiry),
        ("merchantType", 18, r => r.MerchantType),
        ("entryMode", 22, r => r.EntryMode),
        ("rrn", 37, r => r.Rrn),
        ("terminalId", 41, r => r.TerminalId),
        ("merchantId", 42, r => r.MerchantId),
        ("cardAcceptor", 43, r => r.CardAcceptor),
        ("currencyCode", 49, r => r.CurrencyCode),
    ];

    public static async Task<Results<Ok<TransactionResponse>, ValidationProblem>> HandleAsync(
        TransactionRequest request, TransactionHandler handler, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        // Static sınıf ILogger<T>'ye tip olarak verilemediği için kategori adı elle verilir.
        var logger = loggerFactory.CreateLogger(typeof(TransactionEndpoint).FullName!);
        logger.LogInformation("{Method}: request received MTI {Mti} TID {TerminalId} STAN {Stan} PAN {Pan}",
            nameof(HandleAsync), request.Mti, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber));

        var errors = new Dictionary<string, string[]>();
        if (request.Mti is not { } mti || !IsoMessage.IsValidMti(mti) || !new IsoMessage(mti).IsRequest)
        {
            logger.LogWarning("{Method}: invalid MTI {Mti} - responding 400", nameof(HandleAsync), request.Mti);
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["mti"] = ["Must be a 4-digit request MTI, e.g. 0200."]
            });
        }

        var message = new IsoMessage(mti);
        foreach (var (name, field, read) in Fields)
        {
            if (read(request) is not { } value)
            {
                if (mti == TransactionHandler.FinancialRequestMti && TransactionHandler.RequiredFinancialFields.Contains(field))
                    errors[name] = [$"F{field} is required."];
                continue;
            }

            if (Iso87Fields.Default[field].Validate(value) is { } reason)
                errors[name] = [$"F{field}: {reason}"];
            else
                message.Set(field, value);
        }

        if (errors.Count > 0)
        {
            logger.LogWarning("{Method}: validation failed STAN {Stan} PAN {Pan} fields {Fields} - responding 400",
                nameof(HandleAsync), request.Stan, PanMask.Mask(request.CardNumber), string.Join(", ", errors.Keys));
            return TypedResults.ValidationProblem(errors);
        }

        var response = await handler.ProcessAsync(message, cancellationToken);
        logger.LogInformation("{Method}: responding {ResponseMti} STAN {Stan} F39 {ResponseCode}",
            nameof(HandleAsync), response.Mti, response[11], response[39]);
        return TypedResults.Ok(new TransactionResponse(response.Mti, response[39]!, response[11], response[37]));
    }
}
