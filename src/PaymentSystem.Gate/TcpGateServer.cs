using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate;

// POS bağlantılarını kabul eder ve her biri için "çerçeve oku -> işle -> çerçeve yaz" döngüsünü çalıştırır.
// Bir POS bağlantısını açık tutup üzerinden birçok mesaj gönderebilir.
public sealed class TcpGateServer(TransactionHandler handler, IOptions<GateOptions> options, ILogger<TcpGateServer> logger)
    : BackgroundService
{
    private readonly TaskCompletionSource<int> _boundPort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Dinleyici açıldığında tamamlanır; Port = 0 iken çağıran taraf hangi portun seçildiğini buradan öğrenir.
    public Task<int> BoundPort => _boundPort.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, options.Value.Port);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _boundPort.TrySetException(ex);
            throw;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _boundPort.TrySetResult(port);
        logger.LogInformation("Gate listening for ISO8583 on TCP port {Port}", port);

        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                // Her bağlantı kendi görevinde (task) işlenir; böylece yavaş bir POS diğerlerini bekletmez.
                _ = HandleConnectionAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint;
        logger.LogInformation("POS connected: {Remote}", remote);
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                while (await IsoFraming.ReadAsync(stream, cancellationToken) is { } request)
                {
                    var response = await handler.HandleAsync(request, cancellationToken);
                    if (response is null) break;
                    await IsoFraming.WriteAsync(stream, response, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException)
        {
            logger.LogWarning("Connection {Remote} dropped: {Message}", remote, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error on connection {Remote}", remote);
        }
        logger.LogInformation("POS disconnected: {Remote}", remote);
    }
}
