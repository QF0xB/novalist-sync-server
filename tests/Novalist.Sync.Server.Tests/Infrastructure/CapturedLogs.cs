using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// Collects every log entry the server writes, at every level, so tests can assert
// that no payload bytes ever reach the logs (security rule 3).
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.Select(p => p.Value).ToList()
                : [];

            entries.Enqueue(new CapturedLogEntry(category, logLevel, formatter(state, exception), exception?.ToString(), values));
        }
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    string? Exception,
    IReadOnlyList<object?> Values);
