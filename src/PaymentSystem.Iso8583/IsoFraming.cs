using System.Buffers.Binary;

namespace PaymentSystem.Iso8583;

// TCP is a byte stream with no message boundaries, so every message is sent as a frame:
//   [length: 2 bytes, big-endian, not counting itself][message bytes]
public static class IsoFraming
{
    public const int HeaderSize = 2;
    public const int MaxMessageLength = 8192;

    // Returns null when the peer closed the connection cleanly between messages.
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
        // TCP may deliver the message in several pieces; keep reading until all of it has arrived.
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
