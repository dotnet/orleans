using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed class SharedCallbackData
{
    public readonly Action<Message> Unregister;
    public readonly ILogger Logger;
    private TimeSpan _responseTimeout;
    public long ResponseTimeoutStopwatchTicks;

    public SharedCallbackData(
        Action<Message> unregister,
        ILogger logger,
        TimeSpan responseTimeout,
        bool cancelOnTimeout,
        bool waitForCancellationAcknowledgement,
        IGrainCallCancellationManager cancellationManager)
    {
        Unregister = unregister;
        Logger = logger;
        ResponseTimeout = responseTimeout;
        CancelRequestOnTimeout = cancelOnTimeout;
        WaitForCancellationAcknowledgement = waitForCancellationAcknowledgement;
        CancellationManager = cancellationManager;
    }

    public TimeSpan ResponseTimeout
    {
        get => _responseTimeout;
        set
        {
            _responseTimeout = value;
            ResponseTimeoutStopwatchTicks = (long)(value.TotalSeconds * Stopwatch.Frequency);
        }
    }

    public IGrainCallCancellationManager CancellationManager { get; internal set; }

    public bool CancelRequestOnTimeout { get; }

    public bool WaitForCancellationAcknowledgement { get; }
}