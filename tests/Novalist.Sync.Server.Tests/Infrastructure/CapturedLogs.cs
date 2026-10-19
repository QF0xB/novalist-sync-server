using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// Collects every log entry the server writes, at every level and with all active
// scopes, so tests can assert that no payload bytes ever reach the logs (security rule 3).
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();
    private readonly AsyncLocal<Scope?> _currentScope = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private static IEnumerable<object?> ValuesOf(object? state) =>
        state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.Select(p => p.Value) : [state];

    private sealed class CapturingLogger(string category, CapturedLogs logs) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            var scope = new Scope(state, logs._currentScope.Value, logs._currentScope);
            logs._currentScope.Value = scope;
            return scope;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopeStates = new List<object>();
            for (var scope = logs._currentScope.Value; scope is not null; scope = scope.Parent)
            {
                scopeStates.Add(scope.State);
            }

            logs._entries.Enqueue(new CapturedLogEntry(
                category,
                logLevel,
                formatter(state, exception),
                exception?.ToString(),
                ValuesOf(state).ToList(),
                string.Join(" | ", scopeStates.Select(s => s.ToString())),
                scopeStates.SelectMany(ValuesOf).ToList()));
        }
    }

    private sealed class Scope(object state, Scope? parent, AsyncLocal<Scope?> current) : IDisposable
    {
        public object State { get; } = state;

        public Scope? Parent { get; } = parent;

        public void Dispose() => current.Value = Parent;
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    string? Exception,
    IReadOnlyList<object?> Values,
    string ScopeText,
    IReadOnlyList<object?> ScopeValues);
