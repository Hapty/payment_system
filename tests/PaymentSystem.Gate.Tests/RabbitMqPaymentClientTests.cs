using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Gate.Payment;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;

namespace PaymentSystem.Gate.Tests;

// Testler için gerçek bir RabbitMQ container'ı başlatır (Docker gerektirir). docker-compose'daki rabbitmq servisiyle aynı imaj.
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Password = "test-password";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4.1-management")
        .WithUsername("payment")
        .WithPassword(Password)
        .Build();

    // Gate'teki gibi şifresiz URI; şifre ayrıca verilir.
    public string Uri => $"amqp://payment@{_container.Hostname}:{_container.GetMappedPublicPort(5672)}";

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }.CreateConnectionAsync();
}

// Payment'ın yerine kuyruğu dinleyen sahte bir cevaplayıcı kurar ve RabbitMqPaymentClient'ı gerçek RabbitMQ üzerinden dener.
public sealed class RabbitMqPaymentClientTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static readonly PaymentRequest Request = new(
        "0200", "4111111111111111", "000000", 15000, "949", "1002143015", "123456", "173015", "1002", "TERM0001",
        "2812", null, "051", "261002123456", "MERCHANT0000001", null, Cvv: null);

    private readonly List<IAsyncDisposable> _disposables = [];
    private IChannel _admin = null!;

    public List<(IReadOnlyBasicProperties Properties, string Body)> Received { get; } = [];

    public async Task InitializeAsync()
    {
        var connection = await rabbit.ConnectAsync();
        _admin = await connection.CreateChannelAsync();
        _disposables.Add(connection);
        // Önceki testten kalan istek bu testin cevaplayıcısına düşmesin.
        await _admin.QueueDeclareAsync(RabbitMqPaymentClient.QueueName, durable: true, exclusive: false, autoDelete: false);
        await _admin.QueuePurgeAsync(RabbitMqPaymentClient.QueueName);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
    }

    private ListLogger<RabbitMqPaymentClient> Logger { get; } = new();

    private RabbitMqPaymentClient CreateClient(int timeoutSeconds = 5, string? uri = null)
    {
        var client = new RabbitMqPaymentClient(
            Options.Create(new GateOptions
            {
                RabbitMqUri = uri ?? rabbit.Uri,
                RabbitMqPassword = RabbitMqFixture.Password,
                PaymentTimeoutSeconds = timeoutSeconds
            }),
            Logger);
        _disposables.Add(client);
        return client;
    }

    // Payment'ı taklit eder: kuyruktaki her isteğe, gecikme sonrasında verilen gövdeyle cevap verir.
    private async Task RespondWithAsync(string replyBody, TimeSpan delay = default)
    {
        var connection = await rabbit.ConnectAsync();
        _disposables.Add(connection);
        var channel = await connection.CreateChannelAsync();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            Received.Add((delivery.BasicProperties, Encoding.UTF8.GetString(delivery.Body.Span)));
            await Task.Delay(delay);
            await channel.BasicPublishAsync(string.Empty, delivery.BasicProperties.ReplyTo!, mandatory: false,
                new BasicProperties { CorrelationId = delivery.BasicProperties.CorrelationId }, Encoding.UTF8.GetBytes(replyBody));
        };
        await channel.BasicConsumeAsync(RabbitMqPaymentClient.QueueName, autoAck: true, consumer);
    }

    [Fact]
    public async Task Success_ReturnsPaymentCodeAndSendsJsonWithTtl()
    {
        await RespondWithAsync("""{ "responseCode": "51" }""");

        var result = await CreateClient(timeoutSeconds: 5).AuthorizeAsync(Request, CancellationToken.None);

        Assert.Equal("51", result.ResponseCode);
        var (properties, body) = Assert.Single(Received);
        Assert.Contains("\"cardNumber\":\"4111111111111111\"", body);
        Assert.Contains("\"amountMinor\":15000", body);
        Assert.Equal("5000", properties.Expiration);
        Assert.StartsWith("amq.rabbitmq.reply-to", properties.ReplyTo);
        Assert.False(string.IsNullOrEmpty(properties.CorrelationId));
        Assert.True(properties.IsTimestampPresent());
    }

    [Fact]
    public async Task Success_LogsQueueMessageWithMaskedPan()
    {
        await RespondWithAsync("""{ "responseCode": "00" }""");

        await CreateClient().AuthorizeAsync(Request, CancellationToken.None);

        // Kuyruğa giden gövdede kart numarası tam, logda maskeli.
        Assert.Contains("\"cardNumber\":\"4111111111111111\"", Assert.Single(Received).Body);
        var sent = Assert.Single(Logger.Messages, m => m.StartsWith("PublishAsync: sent"));
        Assert.Contains("\"cardNumber\":\"411111********11\"", sent);
        Assert.Contains("\"amountMinor\":15000", sent);
        Assert.Contains(Logger.Messages, m => m.StartsWith("AuthorizeAsync: reply") && m.Contains("F39 00"));
        Assert.DoesNotContain(Logger.Messages, m => m.Contains("4111111111111111"));
    }

    [Fact]
    public async Task SeveralConcurrentRequests_EachGetTheirOwnReply()
    {
        await RespondWithAsync("""{ "responseCode": "00" }""");
        var client = CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.AuthorizeAsync(Request, CancellationToken.None)));

        Assert.All(results, r => Assert.Equal("00", r.ResponseCode));
        Assert.Equal(10, Received.Select(r => r.Properties.CorrelationId).Distinct().Count());
    }

    [Theory]
    [InlineData("""{ "responseCode": "0" }""")]     // F39 için çok kısa
    [InlineData("""{ "responseCode": "ABC" }""")]   // F39 için çok uzun
    [InlineData("""{ }""")]
    [InlineData("not json")]
    public async Task InvalidReply_Returns91(string reply)
    {
        await RespondWithAsync(reply);

        Assert.Equal("91", (await CreateClient().AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Fact]
    public async Task NoPaymentListening_TimesOutWith91AndRequestExpiresFromQueue()
    {
        var result = await CreateClient(timeoutSeconds: 1).AuthorizeAsync(Request, CancellationToken.None);

        Assert.Equal("91", result.ResponseCode);
        // TTL dolduğu için istek kuyruktan silinir; Payment sonradan açılsa bile onu işleyip para düşemez.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await _admin.MessageCountAsync(RabbitMqPaymentClient.QueueName) > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(0u, await _admin.MessageCountAsync(RabbitMqPaymentClient.QueueName));
    }

    [Fact]
    public async Task LateReply_IsDiscarded()
    {
        await RespondWithAsync("""{ "responseCode": "00" }""", delay: TimeSpan.FromMilliseconds(1500));
        var client = CreateClient(timeoutSeconds: 1);

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
        await Task.Delay(1000);   // geç cevap gelir ve atılır

        Assert.Single(Received);
    }

    [Fact]
    public async Task BrokerUnreachable_Returns91()
    {
        var client = CreateClient(timeoutSeconds: 2, uri: "amqp://payment@127.0.0.1:1");

        Assert.Equal("91", (await client.AuthorizeAsync(Request, CancellationToken.None)).ResponseCode);
    }

    [Fact]
    public async Task GateShuttingDown_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient(timeoutSeconds: 10).AuthorizeAsync(Request, cts.Token));
    }
}
