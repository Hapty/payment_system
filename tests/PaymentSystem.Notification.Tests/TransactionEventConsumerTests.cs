using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Notification.Email;
using PaymentSystem.Notification.Events;
using Testcontainers.Kafka;

namespace PaymentSystem.Notification.Tests;

// Testler için gerçek bir Kafka (KRaft, docker-compose'dakiyle aynı imaj) başlatır. (Docker gerektirir.)
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder("apache/kafka:3.9.0").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

// Payment'ın yerine topic'e ham olay yazar; TransactionEventConsumer gerçek Kafka ile, sahte e-posta göndericiyle çalışır.
public class TransactionEventConsumerTests(KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _topic = $"events-{Guid.NewGuid():N}";
    private readonly FakeEmailSender _sender = new();
    private readonly ListLogger<TransactionEventConsumer> _logger = new();

    private TransactionEventConsumer Consumer(string? bootstrapServers = null) => new(
        Options.Create(new KafkaOptions { BootstrapServers = bootstrapServers ?? kafka.BootstrapServers, Topic = _topic, GroupId = $"test-{Guid.NewGuid()}" }),
        new TransactionNotifier(_sender, Options.Create(new SmtpOptions { User = "gonderen@gmail.com", RetryDelaySeconds = 0 }),
            TimeProvider.System, NullLogger<TransactionNotifier>.Instance),
        _logger);

    private async Task ProduceAsync(params string[] values)
    {
        using var producer = new ProducerBuilder<string?, string>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        foreach (var value in values)
            await producer.ProduceAsync(_topic, new Message<string?, string> { Key = "1000000001", Value = value });
    }

    private static string Event(TransactionCompleted transaction) => JsonSerializer.Serialize(transaction, Json);

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met in 30s");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task ApprovedSale_IsEmailedAndUnreadableOrOtherEventsAreSkipped()
    {
        // Olaylar consumer başlamadan yazılır: yeni group topic'i baştan okur, hiçbir olay kaçmaz.
        await ProduceAsync(
            "not json",
            "null",
            Event(TestEvents.ApprovedSale(Guid.NewGuid()) with { ResponseCode = "51" }),
            Event(TestEvents.ApprovedSale()));

        using var consumer = Consumer();
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => _sender.Sent.Count == 1);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }

        Assert.Equal("musteri@example.com", Assert.Single(_sender.Sent).To);
        Assert.Contains(_logger.Messages, m => m.StartsWith("HandleAsync:") && m.Contains("is not valid JSON"));
        Assert.Equal(2, _logger.Messages.Count(m => m.StartsWith("HandleAsync:") && m.EndsWith("skipped - unreadable event")));
    }

    [Fact]
    public async Task EventsProducedWhileListening_AreEmailed()
    {
        using var consumer = Consumer();
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await consumer.Listening.WaitAsync(TimeSpan.FromSeconds(30));
            await ProduceAsync(Event(TestEvents.ApprovedSale(Guid.NewGuid())), Event(TestEvents.ApprovedSale(Guid.NewGuid())));
            await WaitForAsync(() => _sender.Sent.Count == 2);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }

        Assert.Contains(_logger.Messages, m => m.StartsWith("ConsumeAsync: listening on"));
    }

    [Fact]
    public async Task BrokerUnreachable_StopsCleanly()
    {
        using var consumer = Consumer(bootstrapServers: "127.0.0.1:1");

        await consumer.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await consumer.StopAsync(CancellationToken.None);

        Assert.False(consumer.Listening.IsCompleted);
        Assert.Empty(_sender.Sent);
    }
}
