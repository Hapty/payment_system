using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate;

// POST /api/transactions gövdesi: bir finansal isteğin ISO8583 alanları. JSON'da alan numarasıyla gelir ("F2", "F3" ...);
// C# tarafında okunabilir isimler kullanılır. cvv ISO alanı olmadığı için adıyla gelir.
public sealed record TransactionRequest(
    [property: JsonPropertyName("mti"), Description("İstek MTI'ı, örn. 0200 (finansal istek)")] string? Mti,
    [property: JsonPropertyName("F2"), Description("Kart numarası (PAN)")] string? CardNumber,
    [property: JsonPropertyName("F3"), Description("İşlem kodu: 000000 satış, 310000 bakiye sorgu, 400000 transfer")] string? ProcessingCode,
    [property: JsonPropertyName("F4"), Description("Tutar, kuruş cinsinden: 15000 = 150,00")] long? AmountMinor,
    [property: JsonPropertyName("F49"), Description("Para birimi (ISO 4217 sayısal), örn. 949 = TRY")] string? CurrencyCode,
    [property: JsonPropertyName("F7"), Description("İletim tarih-saati, MMDDhhmmss")] string? TransmissionDateTime,
    [property: JsonPropertyName("F11"), Description("STAN (sistem izleme numarası), 6 hane")] string? Stan,
    [property: JsonPropertyName("F12"), Description("Yerel saat, hhmmss")] string? LocalTime,
    [property: JsonPropertyName("F13"), Description("Yerel tarih, yyyymmdd; ISO mesajına MMDD olarak girer")] string? LocalDate,
    [property: JsonPropertyName("F41"), Description("Terminal no")] string? TerminalId,
    [property: JsonPropertyName("F14"), Description("Kart son kullanma tarihi, YYMM")] string? CardExpiry,
    [property: JsonPropertyName("F18"), Description("İşyeri tipi (MCC)")] string? MerchantType,
    [property: JsonPropertyName("F22"), Description("Kart okuma şekli (entry mode), örn. 051 = çip")] string? EntryMode,
    [property: JsonPropertyName("F37"), Description("RRN (referans no)")] string? Rrn,
    [property: JsonPropertyName("F42"), Description("İşyeri no, sabit 15 karakter; Merchant tablosunda aktif olmalı")] string? MerchantId,
    [property: JsonPropertyName("F43"), Description("İşyeri adı / yeri")] string? CardAcceptor,
    [property: JsonPropertyName("F48"), Description("Bu sistemde: para transferinde alıcı kart no")] string? RecipientCardNumber = null,
    // CVV2 (kartın arkası): ISO8583:1987'de standart alanı yok, Payment'a ayrı alan olarak gider
    [property: JsonPropertyName("cvv"), Description("CVV2, 3 ya da 4 hane (ISO alanı değil)")] string? Cvv = null);

// ResponseCode = F39. Onay da red de (örn. 51 yetersiz bakiye) 200 ile döner.
public sealed record TransactionResponse(
    [property: JsonPropertyName("mti"), Description("Cevap MTI'ı, örn. 0210")] string Mti,
    [property: JsonPropertyName("F39"), Description("Cevap kodu: 00 onay, diğerleri red (örn. 51 yetersiz bakiye)")] string ResponseCode,
    [property: JsonPropertyName("F11"), Description("İstekteki STAN")] string? Stan);

// İşlemlerin sisteme tek giriş noktası. İstek ISO8583 mesajına çevrilir ve her alan ISO8583 tanımına göre doğrulanır;
// bozuk ya da eksik alan 400 ile, hangi alanda ne sorun olduğu yazılarak döner. Geçerli istek TransactionHandler'a gider.
public static class TransactionEndpoint
{
    // F13 ayrıca ele alınır (bkz. LocalDate): JSON'da yyyymmdd, ISO mesajında MMDD.
    private static readonly (int Field, Func<TransactionRequest, string?> Value)[] Fields =
    [
        (2, r => r.CardNumber),
        (3, r => r.ProcessingCode),
        (4, r => r.AmountMinor?.ToString("D12", CultureInfo.InvariantCulture)),
        (7, r => r.TransmissionDateTime),
        (11, r => r.Stan),
        (12, r => r.LocalTime),
        (14, r => r.CardExpiry),
        (18, r => r.MerchantType),
        (22, r => r.EntryMode),
        (37, r => r.Rrn),
        (41, r => r.TerminalId),
        (42, r => r.MerchantId),
        (43, r => r.CardAcceptor),
        (48, r => r.RecipientCardNumber),
        (49, r => r.CurrencyCode),
    ];

    public static async Task<Results<Ok<TransactionResponse>, ValidationProblem>> HandleAsync(
        TransactionRequest request, TransactionHandler handler, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        // Static sınıf ILogger<T>'ye tip olarak verilemediği için kategori adı elle verilir.
        var logger = loggerFactory.CreateLogger(typeof(TransactionEndpoint).FullName!);
        logger.LogInformation("{Method}: request received MTI {Mti} {Stan} {TerminalId} {Pan}",
            nameof(HandleAsync), request.Mti, IsoLog.Field(11, request.Stan), IsoLog.Field(41, request.TerminalId),
            IsoLog.Field(2, request.CardNumber));

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
        var financial = mti == TransactionHandler.FinancialRequestMti;
        foreach (var (field, read) in Fields)
        {
            if (read(request) is not { } value)
            {
                if (financial && TransactionHandler.RequiredFinancialFields.Contains(field))
                    errors[$"F{field}"] = [$"F{field} is required."];
                continue;
            }

            if (Iso87Fields.Default[field].Validate(value) is { } reason)
                errors[$"F{field}"] = [$"F{field}: {reason}"];
            else
                message.Set(field, value);
        }

        // F13 JSON'da tam tarih (yyyymmdd) gelir: ISO mesajına standart MMDD hali girer, tam hali Payment'a ayrıca gider.
        if (request.LocalDate is { } localDate)
        {
            if (localDate.Length == 8 && localDate.All(char.IsAsciiDigit)
                && DateOnly.TryParseExact(localDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                message.Set(13, localDate[4..]);
            else
                errors["F13"] = ["F13: Must be a date in yyyymmdd format, e.g. 20261002."];
        }
        else if (financial)
            errors["F13"] = ["F13 is required."];

        // CVV2 3 hane (Visa, Mastercard, Troy) ya da 4 hane (Amex). Değeri hiçbir yere loglanmaz.
        if (request.Cvv is { } cvv && (cvv.Length is not (3 or 4) || !cvv.All(char.IsAsciiDigit)))
            errors["cvv"] = ["Must be 3 or 4 digits."];

        if (errors.Count > 0)
        {
            logger.LogWarning("{Method}: validation failed {Stan} {Pan} fields {Fields} - responding 400",
                nameof(HandleAsync), IsoLog.Field(11, request.Stan), IsoLog.Field(2, request.CardNumber), IsoLog.Keys(errors.Keys));
            return TypedResults.ValidationProblem(errors);
        }

        var response = await handler.ProcessAsync(message, request.Cvv, request.LocalDate, cancellationToken);
        logger.LogInformation("{Method}: responding {ResponseMti} {Stan} {ResponseCode}",
            nameof(HandleAsync), response.Mti, IsoLog.Field(11, response[11]), IsoLog.Field(39, response[39]));
        return TypedResults.Ok(new TransactionResponse(response.Mti, response[39]!, response[11]));
    }
}
