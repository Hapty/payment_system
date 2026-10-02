using System.Net.Http.Json;
using System.Text.Json;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Payment;

// Finansal istekleri Payment servisine iletir: JSON gövdeli POST {PaymentBaseUrl}/api/transactions.
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
            // Bu kod doğrudan F39'a yazılır, bu yüzden o alana uymalı (2 harf/rakam).
            if (body?.ResponseCode is { } code && Iso87Fields.Default[39].Validate(code) is null)
                return body;

            logger.LogWarning("Payment service returned an invalid response code {Code} for STAN {Stan}", body?.ResponseCode, request.Stan);
            return Unavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Biz iptal etmediğimiz halde TaskCanceledException geldiyse HttpClient.Timeout süresi dolmuştur.
            logger.LogWarning("Payment service unreachable for STAN {Stan} ({Error}: {Message}) - responding 91",
                request.Stan, ex.GetType().Name, ex.Message);
            return Unavailable;
        }
    }
}
