using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NeoHub.Tests.Infrastructure;

public sealed record LogEntry(string Category, LogLevel Level, string Template, string Text, Exception? Exception);

/// <summary>Captures every log entry (all levels) with its message template and rendered text.</summary>
public sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CaptureLogger : ILogger
    {
        private readonly CaptureLoggerProvider _owner;
        private readonly string _category;

        public CaptureLogger(CaptureLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value?.ToString() ?? string.Empty
                : string.Empty;
            _owner._entries.Enqueue(new LogEntry(_category, logLevel, template, formatter(state, exception), exception));
        }
    }
}
