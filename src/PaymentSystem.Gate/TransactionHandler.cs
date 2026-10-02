using System.Diagnostics;
using PaymentSystem.Gate.Payment;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate;

// The Gate's core, independent of sockets: request bytes in, response bytes out.
public sealed class TransactionHandler(IPaymentClient paymentClient, ILogger<TransactionHandler> logger)
{
    private static readonly int[] RequiredFinancialFields = [2, 3, 4, 7, 11, 12, 13, 41, 49];
    private static readonly int[] EchoedFinancialFields = [2, 3, 4, 7, 11, 12, 13, 37, 41, 42, 49];
    private static readonly int[] EchoedNetworkFields = [7, 11, 70];

    private readonly IsoMessagePacker _packer = IsoMessagePacker.Default;

    // Returns the response to send back, or null when the message is unusable and the connection should be closed.
    public async Task<byte[]?> HandleAsync(byte[] data, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        IsoMessage request;
        try
        {
            request = _packer.Unpack(data);
        }
        catch (IsoFormatException ex) when (ex.Mti is { } mti && new IsoMessage(mti).IsRequest)
        {
            // The MTI was readable, so the POS can still be told its message was malformed.
            logger.LogWarning("Format error in {Mti} message, field {Field}: {Reason}", mti, ex.FieldNumber, ex.Reason);
            return _packer.Pack(new IsoMessage(new IsoMessage(mti).ToResponseMti()).Set(39, ResponseCodes.FormatError));
        }
        catch (IsoFormatException ex)
        {
            logger.LogWarning("Dropping unreadable message: {Reason}", ex.Message);
            return null;
        }

        if (!request.IsRequest)
        {
            logger.LogWarning("Dropping {Mti}: the Gate only accepts requests", request.Mti);
            return null;
        }

        var response = request.Mti switch
        {
            // Network management (echo test, sign-on): the Gate answers itself to show the link is alive.
            "0800" => Echo(request, EchoedNetworkFields).Set(39, ResponseCodes.Approved),
            "0200" => await AuthorizeAsync(request, cancellationToken),
            _ => new IsoMessage(request.ToResponseMti()).Set(39, ResponseCodes.InvalidTransaction)
        };

        logger.LogInformation("{RequestMti} -> {ResponseMti} STAN {Stan} PAN {Pan} F39 {ResponseCode} in {ElapsedMs} ms",
            request.Mti, response.Mti, request[11], PanMask.Mask(request[2]), response[39], stopwatch.ElapsedMilliseconds);
        return _packer.Pack(response);
    }

    private async Task<IsoMessage> AuthorizeAsync(IsoMessage request, CancellationToken cancellationToken)
    {
        var missing = RequiredFinancialFields.Where(f => !request.Has(f)).ToArray();
        if (missing.Length > 0)
        {
            logger.LogWarning("0200 STAN {Stan} is missing required fields {Fields}", request[11], string.Join(", ", missing));
            return Echo(request, EchoedFinancialFields).Set(39, ResponseCodes.FormatError);
        }

        var result = await paymentClient.AuthorizeAsync(ToPaymentRequest(request), cancellationToken);
        return Echo(request, EchoedFinancialFields).Set(39, result.ResponseCode);
    }

    // Starts the response with the request fields the POS uses to match the answer to its request.
    private static IsoMessage Echo(IsoMessage request, int[] fields)
    {
        var response = new IsoMessage(request.ToResponseMti());
        foreach (var field in fields)
        {
            if (request[field] is { } value)
                response.Set(field, value);
        }
        return response;
    }

    // Only called after the required fields were checked, so the ! below are safe.
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
