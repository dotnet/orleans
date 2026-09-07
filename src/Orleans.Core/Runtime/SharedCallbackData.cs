using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed class SharedCallbackData
{
    public readonly Action<Message> Unregister;
    public readonly ILogger Logger;
    public readonly TimeProvider TimeProvider;
    private TimeSpan _responseTimeout;
    public long ResponseTimeoutTimestampTicks;

    public SharedCallbackData(
        Action<Message> unregister,
        ILogger logger,
        TimeProvider timeProvider,
        TimeSpan responseTimeout,
        bool cancelOnTimeout,
        bool waitForCancellationAcknowledgement,
        IGrainCallCancellationManager? cancellationManager)
    {
        Unregister = unregister;
        Logger = logger;
        TimeProvider = timeProvider;
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
            ResponseTimeoutTimestampTicks = GetTimestampTicks(value);
        }
    }

    public long GetTimestampTicks(TimeSpan duration)
    {
        var timestampTicks = (Int128)duration.Ticks * TimeProvider.TimestampFrequency / TimeSpan.TicksPerSecond;
        if (timestampTicks > long.MaxValue)
        {
            return long.MaxValue;
        }

        if (timestampTicks < long.MinValue)
        {
            return long.MinValue;
        }

        return (long)timestampTicks;
    }

    public IGrainCallCancellationManager? CancellationManager { get; internal set; }

    public bool CancelRequestOnTimeout { get; }

    public bool WaitForCancellationAcknowledgement { get; }
}
