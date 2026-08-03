using Microsoft.Extensions.Logging;

namespace Litos.Api.Logs;

/// <summary>
/// ILoggerProvider that forwards Warning-and-above entries from every ILogger&lt;T&gt; in the app
/// into a shared LogStore, so they show up on /logs without any call site needing to know the
/// admin page exists. Registered both on the host's own ILoggerFactory (Program.cs) and on a
/// standalone LoggerFactory used before app.Build() — the two share the same LogStore instance,
/// so early MCP-handshake logs and later request-pipeline logs land in the same place.
/// </summary>
public sealed class InMemoryLoggerProvider(LogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(store, categoryName);

    public void Dispose() { }

    private sealed class InMemoryLogger(LogStore store, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            store.Add(new LogEntry(logLevel, DateTimeOffset.UtcNow, categoryName, formatter(state, exception), exception));
        }
    }
}
