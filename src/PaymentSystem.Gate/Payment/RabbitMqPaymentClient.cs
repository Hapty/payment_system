using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaymentSystem.Iso8583;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace PaymentSystem.Gate.Payment;

// Finansal istekleri RabbitMQ üzerinden Payment servisine iletir ve cevabını bekler (request-reply).
// İstek QueueName kuyruğuna yazılır; Payment cevabı RabbitMQ'nun direct reply-to adresine yazar, CorrelationId ile eşleştirilir.
// Çağıran için senkron kalır: AuthorizeAsync cevap gelene ya da PaymentTimeoutSeconds dolana kadar bekler.
public sealed class RabbitMqPaymentClient(IOptions<GateOptions> options, ILogger<RabbitMqPaymentClient> logger)
    : IPaymentClient, IAsyncDisposable
{
    // Payment'taki TransactionQueueConsumer.QueueName ile aynı olmalı.
    public const string QueueName = "payment.transactions";

    // RabbitMQ'nun hazır cevap adresi: cevap kuyruğu açmadan, isteği yayınlayan kanala doğrudan cevap gelir.
    private const string DirectReplyTo = "amq.rabbitmq.reply-to";

    private static readonly PaymentResponse Unavailable = new(ResponseCodes.PaymentUnavailable);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>> _pending = new();

    // Bağlantı kurma ve yayınlama tek tek yapılır: direct reply-to, cevabı bekleyen kanalla aynı kanaldan yayın ister.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    private TimeSpan Timeout => TimeSpan.FromSeconds(options.Value.PaymentTimeoutSeconds);

    public async Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<PaymentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = reply;
        try
        {
            try
            {
                await PublishAsync(request, correlationId, cancellationToken);
            }
            catch (Exception ex) when (ex is RabbitMQClientException or IOException or SocketException or TimeoutException)
            {
                // BrokerUnreachableException da IOException'dır.
                logger.LogWarning("{Method}: RabbitMQ unreachable for STAN {Stan} ({Error}: {Message}) - responding 91",
                    nameof(AuthorizeAsync), request.Stan, ex.GetType().Name, ex.Message);
                return Unavailable;
            }

            try
            {
                var response = await reply.Task.WaitAsync(Timeout, cancellationToken);
                logger.LogInformation("{Method}: reply {CorrelationId} for STAN {Stan} F39 {ResponseCode}",
                    nameof(AuthorizeAsync), correlationId, request.Stan, response.ResponseCode);
                return response;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("{Method}: no reply {CorrelationId} from Payment within {Timeout}s for STAN {Stan} - responding 91",
                    nameof(AuthorizeAsync), correlationId, options.Value.PaymentTimeoutSeconds, request.Stan);
                return Unavailable;
            }
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private async Task PublishAsync(PaymentRequest request, string correlationId, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var properties = new BasicProperties
        {
            CorrelationId = correlationId,
            ReplyTo = DirectReplyTo,
            ContentType = "application/json",
            // Süresi dolan istek kuyruktan silinir: Gate 91 döndükten sonra Payment onu işleyip para düşmesin.
            // Payment da Timestamp'e bakıp elindeki süresi dolmuş isteği işlemez.
            Expiration = ((long)Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);
            await channel.BasicPublishAsync(string.Empty, QueueName, mandatory: false, properties, body, cancellationToken);
            // Kuyruğa giden gövdenin aynısı; kart numarası maskeli, CVV hiç yazılmaz (sadece gelip gelmediği görünür).
            logger.LogInformation("{Method}: sent {CorrelationId} to queue {Queue}: {Body}", nameof(PublishAsync), correlationId, QueueName,
                JsonSerializer.Serialize(request with
                {
                    CardNumber = PanMask.Mask(request.CardNumber),
                    Cvv = request.Cvv is null ? null : "***"
                }, Json));
        }
        finally
        {
            _lock.Release();
        }
    }

    // İlk istekte ya da bağlantı koptuktan sonraki ilk istekte bağlanır. Böylece RabbitMQ yokken de Gate açılır (istekler 91 alır)
    // ve RabbitMQ geri gelince kendiliğinden toparlanır. _lock tutulurken çağrılır.
    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await CloseAsync();

        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Value.RabbitMqUri),
            // Kopan bağlantıyı kütüphane değil bu sınıf yeniler (bir sonraki istekte), ikisi aynı anda çalışmasın.
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = Timeout
        };
        if (options.Value.RabbitMqPassword is { } password)
            factory.Password = password;

        _connection = await factory.CreateConnectionAsync("payment-gate", cancellationToken);
        var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        // Kuyruk iki taraf da tanımlar (aynı ayarlarla): hangisi önce açılırsa açılsın kuyruk hazır olur.
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnReplyAsync;
        await channel.BasicConsumeAsync(DirectReplyTo, autoAck: true, consumer, cancellationToken);

        _channel = channel;
        logger.LogInformation("{Method}: connected to RabbitMQ, sending transactions to queue {Queue}", nameof(EnsureChannelAsync), QueueName);
        return channel;
    }

    private Task OnReplyAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var correlationId = delivery.BasicProperties.CorrelationId;
        if (correlationId is null || !_pending.TryRemove(correlationId, out var reply))
        {
            // Bekleyen kimse yok: Gate bu isteğe zaten 91 döndü. Payment onayladıysa para düşmüş olabilir (ileride reversal).
            logger.LogWarning("{Method}: discarding late reply {CorrelationId}: {Body}", nameof(OnReplyAsync), correlationId, System.Text.Encoding.UTF8.GetString(delivery.Body.Span));
            return Task.CompletedTask;
        }

        reply.TrySetResult(Parse(delivery.Body.Span));
        return Task.CompletedTask;
    }

    private PaymentResponse Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            // Bu kod doğrudan F39'a yazılır, bu yüzden o alana uymalı (2 harf/rakam).
            if (JsonSerializer.Deserialize<PaymentResponse>(body, Json)?.ResponseCode is { } code
                && Iso87Fields.Default[39].Validate(code) is null)
                return new PaymentResponse(code);
        }
        catch (JsonException)
        {
        }

        logger.LogWarning("{Method}: Payment sent an invalid reply - responding 91", nameof(Parse));
        return Unavailable;
    }

    private async Task CloseAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
        (_channel, _connection) = (null, null);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _lock.Dispose();
    }
}
