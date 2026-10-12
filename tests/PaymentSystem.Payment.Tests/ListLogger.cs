using Microsoft.Extensions.Logging;

namespace PaymentSystem.Payment.Tests;

// Yazılan log satırlarını toplar; testler kart numarasının loglara maskeli düştüğünü bununla kontrol eder.
public sealed class ListLogger<T> : ILogger<T>, ILoggerFactory
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) return _messages.ToArray(); }
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages) _messages.Add(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}
