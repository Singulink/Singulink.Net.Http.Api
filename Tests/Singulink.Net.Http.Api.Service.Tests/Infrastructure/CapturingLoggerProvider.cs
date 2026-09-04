using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Logger provider that captures log entries in memory for assertions.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get {
            lock (_entries)
                return [.. _entries];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(category, logLevel, formatter(state, exception), exception);

            lock (provider._entries)
                provider._entries.Add(entry);
        }
    }
}

public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
