using System.Collections.Concurrent;
using Avalonia.Logging;

namespace Keel.Desktop.Tests;

/// <summary>Collects Avalonia warnings and errors (binding failures, missing resources, layout errors).</summary>
public sealed class LogCapture : ILogSink
{
    public static LogCapture Instance { get; } = new();

    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public void Clear() => _messages.Clear();

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        _messages.Enqueue($"[{level}] {area}: {messageTemplate} ({source})");

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        _messages.Enqueue($"[{level}] {area}: {messageTemplate} [{string.Join(", ", propertyValues)}] ({source})");
}
