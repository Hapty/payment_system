using PaymentSystem.Notification;
using PaymentSystem.Notification.Email;
using PaymentSystem.Notification.Events;

var builder = Host.CreateApplicationBuilder(args);

// Payment ile aynı tek satırlık, zaman damgalı konsol logu.
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

// Payment'ın işlem olaylarını Kafka'dan okur, onaylı satışlarda SMTP ile e-posta atar.
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Smtp:Host is not configured.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Sender), "Smtp:User (SMTP_USER) or Smtp:From (SMTP_FROM) is not configured.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<TransactionNotifier>();
builder.Services.AddHostedService<TransactionEventConsumer>();

builder.Build().Run();
