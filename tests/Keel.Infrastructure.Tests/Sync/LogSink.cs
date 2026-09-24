using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>Collects every formatted log line (with exception text) so tests can assert nothing secret is logged.</summary>
public sealed class LogSink : ILoggerFactory
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public string All => string.Join('\n', Lines);

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    internal sealed class SinkLogger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}

/// <summary>A typed logger over <see cref="LogSink"/>.</summary>
public sealed class SinkLogger<T>(LogSink sink) : ILogger<T>
{
    private readonly ILogger _inner = sink.CreateLogger(typeof(T).Name);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
