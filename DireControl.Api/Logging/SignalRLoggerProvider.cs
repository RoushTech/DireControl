namespace DireControl.Api.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> that mirrors every log record (subject to the
/// configured <c>Logging:LogLevel</c> filters, exactly like the console) into the
/// <see cref="LogStreamBroadcaster"/> so it can be streamed to the frontend.
/// </summary>
[ProviderAlias("SignalR")]
public sealed class SignalRLoggerProvider(LogStreamBroadcaster broadcaster) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SignalRLogger(categoryName, broadcaster);

    public void Dispose() { }
}

internal sealed class SignalRLogger(string category, LogStreamBroadcaster broadcaster) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None
        // Skip SignalR's own plumbing — broadcasting an entry can itself produce
        // SignalR logs, which would otherwise loop straight back into the stream.
        && !category.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal)
        && !category.StartsWith("Microsoft.AspNetCore.Http.Connections", StringComparison.Ordinal);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
            return;

        broadcaster.Publish(
            DateTime.UtcNow,
            logLevel.ToString(),
            category,
            message,
            exception?.ToString());
    }
}
