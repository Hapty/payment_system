using PaymentSystem.Gate.Payment;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

internal static class TestMessages
{
    public static IsoMessage Sale() => new IsoMessage("0200")
        .Set(2, "4111111111111111")
        .Set(3, "000000")
        .Set(4, "000000015000")
        .Set(7, "1002143015")
        .Set(11, "123456")
        .Set(12, "173015")
        .Set(13, "1002")
        .Set(14, "2812")
        .Set(22, "051")
        .Set(37, "261002123456")
        .Set(41, "TERM0001")
        .Set(42, "MERCHANT0000001")
        .Set(49, "949");

    public static IsoMessage Echo() => new IsoMessage("0800")
        .Set(7, "1002143015")
        .Set(11, "000001")
        .Set(70, "301");

    public static byte[] Pack(IsoMessage message) => IsoMessagePacker.Default.Pack(message);

    public static IsoMessage Unpack(byte[] data) => IsoMessagePacker.Default.Unpack(data);
}

internal sealed class FakePaymentClient(string responseCode = "00") : IPaymentClient
{
    public List<PaymentRequest> Requests { get; } = [];

    public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new PaymentResponse(responseCode));
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Received.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
        return await respond(request, cancellationToken);
    }
}

// Her okumada en fazla 1 bayt verir; mesajı birçok TCP parçasına bölen yavaş bir ağı taklit eder.
internal sealed class OneByteAtATimeStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}
