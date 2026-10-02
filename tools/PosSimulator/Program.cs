using System.Globalization;
using System.Net.Sockets;
using PaymentSystem.Iso8583;

// A tiny POS terminal: builds one ISO8583 request, sends it to the Gate over TCP and prints the answer.
//   dotnet run --project tools/PosSimulator -- sale 4111111111111111 150.00 [--host localhost] [--port 8583] [--expiry 2812]

var positional = new List<string>();
var options = new Dictionary<string, string> { ["host"] = "localhost", ["port"] = "8583", ["expiry"] = "2812" };
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length) options[args[i][2..]] = args[++i];
    else positional.Add(args[i]);
}

IsoMessage request;
byte[] bytes;
try
{
    request = positional switch
    {
        ["echo"] => NetworkMessage(),
        ["sale", var pan, var amount] => FinancialMessage("000000", pan, ParseAmount(amount)),
        ["balance", var pan] => FinancialMessage("310000", pan, 0),
        ["transfer", var pan, var amount] => FinancialMessage("400000", pan, ParseAmount(amount)),
        _ => throw new ArgumentException("Unknown command.")
    };
    bytes = IsoMessagePacker.Default.Pack(request);   // also validates the values, e.g. a PAN with letters
}
catch (Exception ex) when (ex is ArgumentException or IsoFormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("""
        Usage:
          echo                       network echo test (0800)
          sale <pan> <amount>        purchase (0200, F3=000000)
          balance <pan>              balance inquiry (0200, F3=310000)
          transfer <pan> <amount>    money transfer (0200, F3=400000)
        Options: --host localhost --port 8583 --expiry YYMM
        """);
    return 1;
}

PrintMessage("REQUEST", request);
Console.WriteLine($"  {bytes.Length} bytes on the wire (after the 2-byte length header):");
PrintHexDump(bytes);

try
{
    using var client = new TcpClient();
    await client.ConnectAsync(options["host"], int.Parse(options["port"]));
    var stream = client.GetStream();
    await IsoFraming.WriteAsync(stream, bytes);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var answer = await IsoFraming.ReadAsync(stream, timeout.Token);
    if (answer is null)
    {
        Console.WriteLine("\nThe Gate closed the connection without answering.");
        return 2;
    }

    var response = IsoMessagePacker.Default.Unpack(answer);
    PrintMessage("RESPONSE", response);
    Console.WriteLine($"\n  F39 = {response[39]} -> {Describe(response[39])}");
    return response[39] == "00" ? 0 : 3;
}
catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
{
    Console.Error.WriteLine($"\nCould not talk to the Gate at {options["host"]}:{options["port"]}: {ex.Message}");
    return 2;
}

IsoMessage NetworkMessage()
{
    var now = DateTime.UtcNow;
    return new IsoMessage("0800")
        .Set(7, now.ToString("MMddHHmmss"))
        .Set(11, NewStan())
        .Set(70, "301");    // 301 = echo test
}

IsoMessage FinancialMessage(string processingCode, string pan, long amountMinor)
{
    var now = DateTime.Now;
    var stan = NewStan();
    return new IsoMessage("0200")
        .Set(2, pan)
        .Set(3, processingCode)
        .Set(4, amountMinor.ToString("D12"))
        .Set(7, now.ToUniversalTime().ToString("MMddHHmmss"))
        .Set(11, stan)
        .Set(12, now.ToString("HHmmss"))
        .Set(13, now.ToString("MMdd"))
        .Set(14, options["expiry"])
        .Set(18, "5999")
        .Set(22, "051")                 // 05 = chip, 1 = PIN entry capability
        .Set(37, now.ToString("yyMMdd") + stan)
        .Set(41, "TERM0001")
        .Set(42, "MERCHANT0000001")
        .Set(43, "PAYMENT SYSTEM POS SIMULATOR".PadRight(40))
        .Set(49, "949");                // 949 = TRY
}

static string NewStan() => Random.Shared.Next(1, 1_000_000).ToString("D6");

static long ParseAmount(string text)
{
    // Accept both 150.00 and 150,00.
    if (!decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
        || amount < 0 || decimal.Round(amount, 2) != amount)
        throw new ArgumentException($"'{text}' is not a valid amount such as 150.00.");
    return (long)(amount * 100);
}

static void PrintMessage(string title, IsoMessage message)
{
    Console.WriteLine($"\n{title}  MTI {message.Mti}");
    foreach (var (number, value) in message.Fields)
    {
        var name = Iso87Fields.Default.TryGetValue(number, out var definition) ? definition.Name : "?";
        Console.WriteLine($"  F{number,-3} {name,-40} [{value}]");
    }
}

static void PrintHexDump(byte[] bytes)
{
    for (var offset = 0; offset < bytes.Length; offset += 16)
    {
        var line = bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset));
        var hex = string.Join(' ', line.ToArray().Select(b => b.ToString("X2")));
        var text = new string(line.ToArray().Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.').ToArray());
        Console.WriteLine($"  {offset:X4}  {hex,-47}  {text}");
    }
}

static string Describe(string? code) => code switch
{
    "00" => "Approved",
    "12" => "Invalid transaction (the Gate does not support this MTI)",
    "30" => "Format error (missing or malformed field)",
    "91" => "Payment service unavailable",
    _ => "Declined by the Payment service"
};
