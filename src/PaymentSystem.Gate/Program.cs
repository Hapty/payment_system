using Microsoft.Extensions.Options;
using PaymentSystem.Gate;
using PaymentSystem.Gate.Payment;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<GateOptions>(builder.Configuration.GetSection(GateOptions.SectionName));

builder.Services
    .AddHttpClient<IPaymentClient, HttpPaymentClient>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<GateOptions>>().Value;
        client.BaseAddress = new Uri(options.PaymentBaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.PaymentTimeoutSeconds);
    })
    // The client lives as long as the singleton TransactionHandler, so let the handler recycle
    // connections itself (picks up DNS changes) instead of relying on HttpClientFactory rotation.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Services.AddSingleton<TransactionHandler>();
builder.Services.AddHostedService<TcpGateServer>();

builder.Build().Run();
