using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentSystem.Gate.Payment;

namespace PaymentSystem.Gate.Tests;

public class HttpPaymentClientTests
{
    private static readonly PaymentRequest Request = new(
        "0200", "4111111111111111", "000000", 15000, "949", "1002143015", "123456", "173015", "1002", "TERM0001",
        "2812", null, "051", "261002123456", "MERCHANT0000001", null);

    private static (HttpPaymentClient Client, StubHttpHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://payment/"), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        return (new HttpPaymentClient(http, NullLogger<HttpPaymentClient>.Instance), handler);
    }

    private static Task<HttpResponseMessage> Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(body) });

    [Fact]
    public async Task Success_ReturnsPaymentCodeAndPostsJsonToTransactions()
    {
        var (client, handler) = Create((_, _) => Json(new { responseCode = "51" }));

        var result = await client.AuthorizeAsync(Request, CancellationToken.None);

        Assert.Equal("51", result.ResponseCode);
        var (sent, body) = Assert.Single(handler.Received);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://payment/api/transactions", sent.RequestUri!.ToString());
        Assert.Contains("\"cardNumber\":\"4111111111111111\"", body);
        Assert.Contains("\"amountMinor\":15000", body);
    }

    [Fact]
    public async Task ServerError_Returns91()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Theory]
    [InlineData("0")]       // F39 için çok kısa
    [InlineData("ABC")]     // F39 için çok uzun
    public async Task InvalidResponseCode_Returns91(string code)
    {
        var (client, _) = Create((_, _) => Json(new { responseCode = code }));

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Fact]
    public async Task NonJsonBody_Returns91()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }));

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Fact]
    public async Task ConnectionFailure_Returns91()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("Connection refused"));

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Fact]
    public async Task Timeout_Returns91()
    {
        var (client, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }, timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Fact]
    public async Task GateShuttingDown_PropagatesCancellation()
    {
        var (client, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AuthorizeAsync(Request, cts.Token));
    }
}
