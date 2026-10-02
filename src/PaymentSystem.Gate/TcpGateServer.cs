using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate;

// Accepts POS connections and runs a read-frame -> handle -> write-frame loop for each one.
// A POS may keep its connection open and send many messages over it.
public sealed class TcpGateServer(TransactionHandler handler, IOptions<GateOptions> options, ILogger<TcpGateServer> logger)
    : BackgroundService
{
    private readonly TaskCompletionSource<int> _boundPort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completes once the listener is up; with Port = 0 this is how callers learn which port was picked.
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
                // Each connection is served on its own task so one slow POS does not block the others.
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
