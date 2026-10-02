using System.Net.Http.Json;
using System.Text.Json;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Payment;

// Forwards financial requests to the Payment service: POST {PaymentBaseUrl}/api/transactions with a JSON body.
public sealed class HttpPaymentClient(HttpClient http, ILogger<HttpPaymentClient> logger) : IPaymentClient
{
    private static readonly PaymentResponse Unavailable = new(ResponseCodes.PaymentUnavailable);

    public async Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/transactions", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Payment service answered {StatusCode} for STAN {Stan}", (int)response.StatusCode, request.Stan);
                return Unavailable;
            }

            var body = await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken);
            // The code goes straight into F39, so it must fit that field (2 letters/digits).
            if (body?.ResponseCode is { } code && Iso87Fields.Default[39].Validate(code) is null)
                return body;

            logger.LogWarning("Payment service returned an invalid response code {Code} for STAN {Stan}", body?.ResponseCode, request.Stan);
            return Unavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // TaskCanceledException without our own cancellation means HttpClient.Timeout elapsed.
            logger.LogWarning("Payment service unreachable for STAN {Stan} ({Error}: {Message}) - responding 91",
                request.Stan, ex.GetType().Name, ex.Message);
            return Unavailable;
        }
    }
}
