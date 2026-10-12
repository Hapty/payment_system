using System.Buffers.Binary;

namespace PaymentSystem.Iso8583;

// TCP, mesaj sınırı olmayan bir bayt akışıdır; bu yüzden her mesaj bir çerçeve (frame) içinde gönderilir:
//   [uzunluk: 2 bayt, big-endian, kendisini saymaz][mesaj baytları]
public static class IsoFraming
{
    public const int HeaderSize = 2;
    public const int MaxMessageLength = 8192;

    // Karşı taraf bağlantıyı iki mesajın arasında düzgünce kapattıysa null döner.
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        var read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken);
        if (read == 0) return null;
        if (read < HeaderSize) throw new EndOfStreamException("Connection closed inside the length header.");

        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is 0 or > MaxMessageLength)
            throw new InvalidDataException($"Frame length {length} is outside 1..{MaxMessageLength}.");

        var message = new byte[length];
        // TCP mesajı birkaç parça halinde getirebilir; mesajın tamamı gelene kadar okumaya devam et.
        await stream.ReadExactlyAsync(message, cancellationToken);
        return message;
    }

    public static async Task WriteAsync(Stream stream, byte[] message, CancellationToken cancellationToken = default)
    {
        if (message.Length is 0 or > MaxMessageLength)
            throw new ArgumentException($"Message length {message.Length} is outside 1..{MaxMessageLength}.", nameof(message));

        var frame = new byte[HeaderSize + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame, HeaderSize);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
