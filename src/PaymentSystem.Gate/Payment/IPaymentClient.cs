namespace PaymentSystem.Gate.Payment;

public interface IPaymentClient
{
    // Never throws for Payment-side problems: an unreachable or misbehaving Payment service yields response code 91.
    Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken cancellationToken);
}
