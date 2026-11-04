using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Daemon;

/// <summary>A logger provider that keeps every message the daemon logs, with its level, for a test to read.</summary>
public sealed class RecordingLogger : ILoggerProvider
{
    private readonly Lock gate = new();
    private readonly List<(LogLevel Level, string Message)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLogger owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception.Message}";
            lock (owner.gate)
            {
                owner.entries.Add((logLevel, message));
            }
        }
    }
}
