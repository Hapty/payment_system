using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Security;
using Microsoft.Extensions.Options;
using PaymentSystem.Notification.Email;

namespace PaymentSystem.Notification.Tests;

// Gerçek SMTP yerine Mailpit: e-postayı kabul eder, kimseye göndermez, HTTP API'sinden okunur. (Docker gerektirir.)
// Her kullanıcı / şifreyi kabul eder; böylece gönderimin AUTH adımı da test edilir.
public sealed class MailpitFixture : IAsyncLifetime
{
    private const int SmtpPort = 1025;
    private const int HttpPort = 8025;

    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.21.8")
        .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")
        .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(HttpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/livez")))
        .Build();

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(SmtpPort);
    public HttpClient Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}") };
    }

    public async Task DisposeAsync()
    {
        Api.Dispose();
        await _container.DisposeAsync();
    }

    // Mailpit'teki son e-postanın göndereni, alıcısı, konusu ve düz metin gövdesi.
    public async Task<(string From, string To, string Subject, string Text)> LatestAsync()
    {
        var list = await Api.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        var id = list.GetProperty("messages")[0].GetProperty("ID").GetString();
        var message = await Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}");
        return (message.GetProperty("From").GetProperty("Address").GetString()!,
            message.GetProperty("To")[0].GetProperty("Address").GetString()!,
            message.GetProperty("Subject").GetString()!,
            message.GetProperty("Text").GetString()!);
    }
}

public class SmtpEmailSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private SmtpEmailSender Sender(string? user, string? from = null, int? port = null) => new(Options.Create(new SmtpOptions
    {
        Host = mailpit.Host,
        Port = port ?? mailpit.Port,
        Security = SecureSocketOptions.None,
        User = user,
        Password = "app-password",
        From = from
    }));

    [Fact]
    public async Task Send_WithLogin_DeliversFromUserAddress()
    {
        var email = SaleEmail.Compose(TestEvents.ApprovedSale(), "musteri@example.com");

        await Sender(user: "gonderen@gmail.com").SendAsync(email, CancellationToken.None);

        var (from, to, subject, text) = await mailpit.LatestAsync();
        Assert.Equal(("gonderen@gmail.com", "musteri@example.com"), (from, to));
        Assert.Equal("Kartınızla 1.500,00 TRY tutarında satış yapıldı", subject);
        Assert.Contains("Tarih / saat : 15.03.2026 12:59:59", text);
    }

    [Fact]
    public async Task Send_WithoutLogin_UsesFromAddress()
    {
        await Sender(user: null, from: "bildirim@example.com")
            .SendAsync(new EmailMessage("musteri@example.com", "Test", "Merhaba"), CancellationToken.None);

        Assert.Equal("bildirim@example.com", (await mailpit.LatestAsync()).From);
    }

    [Fact]
    public async Task Send_ServerUnreachable_Throws() =>
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(user: null, from: "bildirim@example.com", port: 1)
            .SendAsync(new EmailMessage("musteri@example.com", "Test", "Merhaba"), CancellationToken.None));
}
