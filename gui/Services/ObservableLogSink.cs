using CloudflareDdns.Gui.Models;
using Serilog.Core;
using Serilog.Events;

namespace CloudflareDdns.Gui.Services;

/// <summary>
/// A Serilog sink that forwards every log event to the UI as a <see cref="LogEntry"/>.
/// Shared across host rebuilds so the live log panel stays continuous even though a fresh
/// DI host is built for each sync/dry-run pass.
/// </summary>
public sealed class ObservableLogSink : ILogEventSink
{
    /// <summary>Raised (on a threadpool thread) for every emitted log event. Marshal to the UI thread.</summary>
    public event Action<LogEntry>? Emitted;

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = logEvent.Level.ToString(),
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString()
        };
        Emitted?.Invoke(entry);
    }
}
