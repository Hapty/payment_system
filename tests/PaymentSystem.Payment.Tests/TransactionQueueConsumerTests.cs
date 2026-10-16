using System.Text;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Transactions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;

namespace PaymentSystem.Payment.Tests;

// Testler için gerçek bir RabbitMQ container'ı başlatır (Docker gerektirir). docker-compose'daki rabbitmq servisiyle aynı imaj.
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4.1-management")
        .WithUsername("payment")
        .WithPassword("test-password")
        .Build();

    public ConnectionFactory Factory(string? uri = null) => new()
    {
        Uri = new Uri(uri ?? _container.GetConnectionString()),
        AutomaticRecoveryEnabled = false,
        ConsumerDispatchConcurrency = TransactionQueueConsumer.Concurrency
    };

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

// Gate'in yerine kuyruğa ham mesaj yazar; TransactionQueueConsumer gerçek RabbitMQ ve gerçek SQL Server ile çalışır.
public class TransactionQueueConsumerTests(SqlServerFixture fixture, RabbitMqFixture rabbit)
    : DatabaseTest(fixture), IClassFixture<RabbitMqFixture>
{
    private const string Pan = "4111111111111111";
    private const string DirectReplyTo = "amq.rabbitmq.reply-to";

    private async Task SeedAsync()
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES ('10', '11', 'Sale');
            INSERT INTO dbo.MtiProcessingCode (Mti, F3_ProcessingCode, Otc, Ots) VALUES ('0200', '000000', '10', '11');
            INSERT INTO dbo.EntryMode (Code, Name, IsContactless) VALUES ('05', 'Chip', 0);
            INSERT INTO dbo.InternalResponseCode (Irc, Description, F39_ResponseCode) VALUES ('0000', 'Approved', '00'), ('3002', 'Insufficient funds', '51');
            INSERT INTO dbo.BankAccount (AccountNo, AccountStatus, Balance) VALUES ('ACC1', 'Active', 1000);
            INSERT INTO dbo.Card (CardNumber, AccountNo, ExpiryDate, ContactlessAllowed) VALUES (@Pan, 'ACC1', '1228', 1);
            """,
            new { Pan });
    }

    private static string SaleJson(long amountMinor = 15000, string stan = "000001") =>
        $$"""
        {
          "mti": "0200", "cardNumber": "{{Pan}}", "processingCode": "000000", "amountMinor": {{amountMinor}},
          "currencyCode": "949", "transmissionDateTime": "0315095959", "stan": "{{stan}}", "localTime": "125959",
          "localDate": "20260315", "terminalId": "TERM0001", "cardExpiry": "2812", "entryMode": "051"
        }
        """;

    private ListLogger<TransactionQueueConsumer> ConsumerLogger { get; } = new();
    private ListLogger<TransactionHandler> HandlerLogger { get; } = new();

    private async Task<T> WithConsumerAsync<T>(Func<Task<T>> test, SqlConnectionFactory? db = null)
    {
        var handler = new TransactionHandler(
            new TransactionRepository(db ?? Db),
            TestParameters.Create(db ?? Db),
            new ExchangeRateCache(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System, Options.Create(new ExchangeRateOptions())),
            TestSecrets.Hasher,
            new RecordingEventPublisher(),
            TimeProvider.System,
            HandlerLogger);
        var consumer = new TransactionQueueConsumer(rabbit.Factory(), handler, TimeProvider.System, ConsumerLogger);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await consumer.Listening.WaitAsync(TimeSpan.FromSeconds(10));
            return await test();
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }

    // Gate gibi isteği yayınlar ve direct reply-to'dan cevabı bekler. Cevap gelmezse null.
    private async Task<(string? CorrelationId, string Body)?> SendAsync(string body, DateTimeOffset? sentAt = null)
    {
        await using var connection = await rabbit.Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var reply = new TaskCompletionSource<(string?, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            reply.TrySetResult((delivery.BasicProperties.CorrelationId, Encoding.UTF8.GetString(delivery.Body.Span)));
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(DirectReplyTo, autoAck: true, consumer);

        var properties = new BasicProperties
        {
            CorrelationId = "request-1",
            ReplyTo = DirectReplyTo,
            Expiration = "10000",
            Timestamp = new AmqpTimestamp((sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds())
        };
        await channel.BasicPublishAsync(string.Empty, TransactionQueueConsumer.QueueName, mandatory: false, properties, Encoding.UTF8.GetBytes(body));

        try
        {
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task<decimal> BalanceAsync()
    {
        await using var connection = Db.Create();
        return await connection.ExecuteScalarAsync<decimal>("SELECT Balance FROM dbo.BankAccount WHERE AccountNo = 'ACC1'");
    }

    private async Task<int> TransactionCountAsync()
    {
        await using var connection = Db.Create();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.DebitTransaction");
    }

    [Fact]
    public async Task Sale_IsProcessedAndAnsweredWithSameCorrelationId()
    {
        await SeedAsync();

        var reply = await WithConsumerAsync(() => SendAsync(SaleJson()));

        Assert.Equal(("request-1", """{"responseCode":"00"}"""), reply);
        Assert.Equal(850m, await BalanceAsync());
    }

    [Fact]
    public async Task Sale_LogsEachStepWithMaskedPan()
    {
        await SeedAsync();

        await WithConsumerAsync(() => SendAsync(SaleJson()));

        var received = Assert.Single(ConsumerLogger.Messages, m => m.StartsWith("DecideAsync: received request-1"));
        Assert.Contains("\"cardNumber\":\"411111********11\"", received);
        Assert.Contains(ConsumerLogger.Messages, m => m.StartsWith("HandleAsync: replied request-1 F39 00"));
        Assert.Contains(HandlerLogger.Messages, m => m.StartsWith("SaleAsync: debited TRY 150") && m.Contains("PAN 411111********11"));
        Assert.Contains(HandlerLogger.Messages, m => m.StartsWith("AuthorizeAsync: Sale") && m.Contains("F39 00"));
        Assert.DoesNotContain(ConsumerLogger.Messages.Concat(HandlerLogger.Messages), m => m.Contains(Pan));
    }

    [Fact]
    public async Task ReceivedRequestLog_ShowsOnlyThatCvvWasSent()
    {
        await SeedAsync();

        await WithConsumerAsync(() => SendAsync(SaleJson().Replace("\"entryMode\": \"051\"", "\"entryMode\": \"051\", \"cvv\": \"987\"")));

        var received = Assert.Single(ConsumerLogger.Messages, m => m.StartsWith("DecideAsync: received request-1"));
        Assert.Contains("\"cvv\":\"***\"", received);
        Assert.DoesNotContain(ConsumerLogger.Messages.Concat(HandlerLogger.Messages), m => m.Contains("987"));
    }

    [Fact]
    public async Task ReceivedRequestLog_MasksRecipientCard()
    {
        await SeedAsync();
        const string recipient = "5406680000000002";

        await WithConsumerAsync(() => SendAsync(SaleJson()
            .Replace("\"processingCode\": \"000000\"", $"\"processingCode\": \"400000\", \"recipientCardNumber\": \"{recipient}\"")));

        var received = Assert.Single(ConsumerLogger.Messages, m => m.StartsWith("DecideAsync: received request-1"));
        Assert.Contains("\"recipientCardNumber\":\"540668********02\"", received);
        Assert.DoesNotContain(ConsumerLogger.Messages.Concat(HandlerLogger.Messages), m => m.Contains(recipient));
    }

    [Fact]
    public async Task Decline_IsAnsweredWithItsCode()
    {
        await SeedAsync();

        var reply = await WithConsumerAsync(() => SendAsync(SaleJson(amountMinor: 500000)));

        Assert.Equal("""{"responseCode":"51"}""", reply?.Body);
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Contains(HandlerLogger.Messages, m => m.StartsWith("SaleAsync: declined") && m.Contains("insufficient funds") && m.Contains("IRC 3002"));
        Assert.Contains(HandlerLogger.Messages, m => m.StartsWith("AuthorizeAsync: Sale") && m.Contains("IRC 3002 F39 51"));
        Assert.Contains(ConsumerLogger.Messages, m => m.StartsWith("DecideAsync: transaction") && m.Contains("not approved: F39 51 Insufficient funds (IRC 3002)")
            && m.Contains("PAN 411111********11 account ACC1"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{ "mti": "0200" }""")]   // zorunlu alanlar eksik
    public async Task InvalidMessage_IsAnsweredWithFormatError(string body)
    {
        var reply = await WithConsumerAsync(() => SendAsync(body));

        Assert.Equal("""{"responseCode":"30"}""", reply?.Body);
        Assert.Equal(0, await TransactionCountAsync());
        Assert.Contains(ConsumerLogger.Messages, m => m.StartsWith("DecideAsync: invalid transaction message"));
    }

    [Fact]
    public async Task ExpiredRequest_IsDroppedWithoutProcessingOrReply()
    {
        await SeedAsync();

        // Gate 10 sn bekler; bir dakika önce gönderilmiş istek için Gate çoktan 91 döndü.
        var reply = await WithConsumerAsync(() => SendAsync(SaleJson(), sentAt: DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null(reply);
        Assert.Equal(0, await TransactionCountAsync());
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task DatabaseUnavailable_IsAnsweredWith91()
    {
        var unreachable = new SqlConnectionFactory("Server=127.0.0.1,1;Database=PaymentSystem;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True");

        var reply = await WithConsumerAsync(() => SendAsync(SaleJson()), unreachable);

        Assert.Equal("""{"responseCode":"91"}""", reply?.Body);
    }

    [Fact]
    public async Task BrokerUnreachable_KeepsRetryingAndStopsCleanly()
    {
        var consumer = new TransactionQueueConsumer(
            rabbit.Factory("amqp://payment:x@127.0.0.1:1"),
            new TransactionHandler(new TransactionRepository(Db), TestParameters.Create(Db), null!, TestSecrets.Hasher, new RecordingEventPublisher(), TimeProvider.System,
                NullLogger<TransactionHandler>.Instance),
            TimeProvider.System,
            NullLogger<TransactionQueueConsumer>.Instance);

        await consumer.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await consumer.StopAsync(CancellationToken.None);

        Assert.False(consumer.Listening.IsCompleted);
        consumer.Dispose();
    }
}
