namespace PaymentSystem.Gate;

// appsettings.json'daki "Gate" bölümünden ya da Gate__RabbitMqUri gibi ortam değişkenlerinden doldurulur.
public class GateOptions
{
    public const string SectionName = "Gate";

    // Payment'a giden kuyruğun bulunduğu RabbitMQ. Şifre URI'de durmaz: RABBITMQ_PASSWORD'den gelir (.env ile aynı anahtar).
    public string RabbitMqUri { get; set; } = "amqp://payment@localhost:5672";

    public string? RabbitMqPassword { get; set; }

    // Payment'tan bu süre içinde cevap gelmezse F39=91. İsteğin kuyruktaki ömrü (TTL) de bu süredir.
    public int PaymentTimeoutSeconds { get; set; } = 10;
}
