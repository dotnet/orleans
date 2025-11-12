using System;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

#pragma warning disable IDE0069 // Disposable fields should be disposed

namespace Orleans.Dashboard.Implementation;

internal sealed class DashboardLogger : ILoggerProvider, ILogger
{
    private readonly NoopDisposable _scope = new();
    private ImmutableArray<Action<EventId, LogLevel, string>> _actions = [];

    public void Add(Action<EventId, LogLevel, string> action) => _actions = _actions.Add(action);

    public void Remove(Action<EventId, LogLevel, string> action) => _actions = _actions.Remove(action);

    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName) => this;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        var currentActions = _actions;

        if (currentActions.Length <= 0)
        {
            return;
        }

        var logMessage = formatter(state, exception);

        foreach (var action in currentActions)
        {
            action(eventId, logLevel, logMessage);
        }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) => _scope;

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
