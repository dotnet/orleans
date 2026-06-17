
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

#nullable disable
namespace Orleans.Runtime;

// Can not use MetricsEventSource because it only supports single listener.
public static class SiloRuntimeMetricsListener
{
    private static readonly MeterListener MeterListener = new();

    private static long _connectedClientCount;
    public static long ConnectedClientCount => _connectedClientCount;
    private static long _messageReceivedTotal;
    public static long MessageReceivedTotal => _messageReceivedTotal;
    private static long _messageSentTotal;
    public static long MessageSentTotal => _messageSentTotal;

    private static readonly string[] MetricNames =
    {
        // orleans
        InstrumentNames.GATEWAY_CONNECTED_CLIENTS,
        InstrumentNames.MESSAGING_RECEIVED_MESSAGES_SIZE,
    };

    static SiloRuntimeMetricsListener()
    {
        MeterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (MetricNames.Contains(instrument.Name))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        MeterListener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
    }

    internal static void Start()
    {
        MeterListener.Start();
    }

    private static void OnMeasurementRecorded(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
    {
        if (instrument.Name == InstrumentNames.GATEWAY_CONNECTED_CLIENTS)
        {
            Interlocked.Add(ref _connectedClientCount, measurement);
        }
        if (instrument.Name == InstrumentNames.MESSAGING_RECEIVED_MESSAGES_SIZE)
        {
            Interlocked.Add(ref _messageReceivedTotal, measurement);
        }
        if (instrument.Name == InstrumentNames.MESSAGING_SENT_MESSAGES_SIZE)
        {
            Interlocked.Add(ref _messageSentTotal, measurement);
        }
    }
}
