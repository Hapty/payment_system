using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Iso8583;

namespace PaymentSystem.Gate.Tests;

// Runs the real TCP server on a free port and talks to it through a real socket, like a POS would.
public class TcpGateServerTests : IAsyncLifetime
{
    private readonly FakePaymentClient _payment = new("00");
    private TcpGateServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var handler = new TransactionHandler(_payment, NullLogger<TransactionHandler>.Instance);
        _server = new TcpGateServer(handler, Options.Create(new GateOptions { Port = 0 }), NullLogger<TcpGateServer>.Instance);
        await _server.StartAsync(CancellationToken.None);
        _port = await _server.BoundPort.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
    }

    private async Task<NetworkStream> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        return client.GetStream();   // disposing the stream also closes the socket
    }

    private static async Task<IsoMessage> RoundTripAsync(NetworkStream stream, IsoMessage request)
    {
        await IsoFraming.WriteAsync(stream, TestMessages.Pack(request));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return TestMessages.Unpack((await IsoFraming.ReadAsync(stream, timeout.Token))!);
    }

    [Fact]
    public async Task SeveralMessagesOnOneConnection_AreAllAnswered()
    {
        await using var stream = await ConnectAsync();

        var echo = await RoundTripAsync(stream, TestMessages.Echo());
        var sale = await RoundTripAsync(stream, TestMessages.Sale());

        Assert.Equal(("0810", "00"), (echo.Mti, echo[39]));
        Assert.Equal(("0210", "00"), (sale.Mti, sale[39]));
        Assert.Single(_payment.Requests);
    }

    [Fact]
    public async Task UnreadableMessage_ClosesConnection()
    {
        await using var stream = await ConnectAsync();

        await IsoFraming.WriteAsync(stream, "garbage"u8.ToArray());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await IsoFraming.ReadAsync(stream, timeout.Token));
    }

    [Fact]
    public async Task InvalidFrame_ClosesConnectionAndServerKeepsRunning()
    {
        await using (var bad = await ConnectAsync())
        {
            await bad.WriteAsync(new byte[] { 0, 0 });   // zero-length frame
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Null(await IsoFraming.ReadAsync(bad, timeout.Token));
        }

        await using var good = await ConnectAsync();
        Assert.Equal("00", (await RoundTripAsync(good, TestMessages.Echo()))[39]);
    }

    [Fact]
    public async Task PortAlreadyInUse_FailsBoundPort()
    {
        var other = new TcpGateServer(
            new TransactionHandler(_payment, NullLogger<TransactionHandler>.Instance),
            Options.Create(new GateOptions { Port = _port }),
            NullLogger<TcpGateServer>.Instance);

        // ExecuteAsync fails before its first await, so the failure surfaces from StartAsync itself.
        await Assert.ThrowsAsync<SocketException>(() => other.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SocketException>(() => other.BoundPort);
        other.Dispose();
    }
}
