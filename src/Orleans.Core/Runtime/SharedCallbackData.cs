using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed class SharedCallbackData
{
    private readonly ICallbackDataTarget _target;
    public readonly ILogger Logger;
    private TimeSpan _responseTimeout;
    public long ResponseTimeoutStopwatchTicks;

    public SharedCallbackData(
        ICallbackDataTarget target,
        ILogger logger,
        TimeSpan responseTimeout,
        bool cancelOnTimeout,
        bool waitForCancellationAcknowledgement,
        IGrainCallCancellationManager? cancellationManager)
    {
        _target = target;
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

    public IGrainCallCancellationManager? CancellationManager { get; internal set; }

    public bool CancelRequestOnTimeout { get; }

    public bool WaitForCancellationAcknowledgement { get; }

    public void Unregister(CallbackData callback) => _target.Unregister(callback);
}
