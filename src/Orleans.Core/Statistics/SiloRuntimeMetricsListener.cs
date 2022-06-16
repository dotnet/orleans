
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime;

// TODO: use a singleton to injecting options
// Can not use MetricsEventSource because it only supports single listener.
public static class SiloRuntimeStatisticsListener
{
    private static readonly MeterListener MeterListener = new();

    private static long _connectedClientCount;
    // ? is volatile read needed?
    public static long ConnectedClientCount => _connectedClientCount;
    private static long _messageReceivedTotal;
    public static long MessageReceivedTotal => _messageReceivedTotal;
    private static long _messageSentTotal;
    public static long MessageSentTotal => _messageSentTotal;

    private static readonly string[] MetricNames =
    {
        // orleans
        StatisticNames.GATEWAY_CONNECTED_CLIENTS,
        StatisticNames.MESSAGING_RECEIVED_MESSAGES_SIZE,
    };

    static SiloRuntimeStatisticsListener()
    {
        MeterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter != Instruments.Meter)
            {
                return;
            }

            if (MetricNames.Contains(instrument.Name))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        MeterListener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
        MeterListener.Start();
    }

    private static void OnMeasurementRecorded(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
    {
        if (instrument == MessagingInstruments.ConnectedClient)
        {
            Interlocked.Add(ref _connectedClientCount, measurement);
        }
        if (instrument == MessagingInstruments.MessageReceivedSizeHistogram)
        {
            Interlocked.Add(ref _messageReceivedTotal, measurement);
        }
        if (instrument == MessagingInstruments.MessageSentSizeHistogram)
        {
            Interlocked.Add(ref _messageSentTotal, measurement);
        }
    }
}
