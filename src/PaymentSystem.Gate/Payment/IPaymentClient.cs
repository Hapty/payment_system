namespace PaymentSystem.Gate.Payment;

public interface IPaymentClient
{
    // Payment tarafındaki sorunlarda asla exception fırlatmaz: ulaşılamayan ya da hatalı cevap veren Payment servisi 91 kodunu üretir.
    Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken cancellationToken);
}
