using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class StorageInstruments
{
    private static readonly Histogram<double> StorageReadHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_READ_LATENCY, "ms");
    private static readonly Histogram<double> StorageWriteHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_WRITE_LATENCY, "ms");
    private static readonly Histogram<double> StorageClearHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_CLEAR_LATENCY, "ms");
    private static readonly Counter<int> StorageReadErrorsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_READ_ERRORS);
    private static readonly Counter<int> StorageWriteErrorsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_WRITE_ERRORS);
    private static readonly Counter<int> StorageClearErrorsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_CLEAR_ERRORS);

    internal static void OnStorageRead(TimeSpan latency, string providerName, string stateName, string stateType)
    {
        if (StorageReadHistogram.Enabled)
        {
            StorageReadHistogram.Record(
                latency.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("provider_name", providerName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateType)
                ]);
        }
    }

    internal static void OnStorageWrite(TimeSpan latency, string providerName, string stateName, string stateType)
    {
        if (StorageWriteHistogram.Enabled)
        {
            StorageWriteHistogram.Record(
                latency.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("provider_name", providerName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateType)
                ]);
        }
    }

    internal static void OnStorageReadError(string providerName, string stateName, string stateType)
    {
        if (StorageReadErrorsCounter.Enabled)
        {
            StorageReadErrorsCounter.Add(1,
                [
                    new KeyValuePair<string, object>("provider_name", providerName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateType)
                ]);
        }
    }

    internal static void OnStorageWriteError(string providerName, string stateName, string stateType)
    {
        if (StorageWriteErrorsCounter.Enabled)
        {
            StorageWriteErrorsCounter.Add(1,
                [
                    new KeyValuePair<string, object>("provider_name", providerName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateType)
                ]);
        }
    }

    internal static void OnStorageDelete(TimeSpan latency, string providerName, string stateName, string stateType)
    {
        if (StorageClearHistogram.Enabled)
        {
            StorageClearHistogram.Record(latency.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("provider_name", providerName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateType)
                ]);
        }
    }

    internal static void OnStorageDeleteError(string providerName, string stateName, string stateType)
    {
        if (StorageClearErrorsCounter.Enabled)
        {
            StorageClearErrorsCounter.Add(1,
                [
                    new KeyValuePair<string, object>("provider_name", providerName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateType)
                ]);
        }
    }
}
