using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

#nullable disable
namespace Orleans.Runtime;

internal sealed class StorageInstruments(OrleansInstruments instruments)
{
    private readonly Histogram<double> _storageReadHistogram = instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_READ_LATENCY, "ms");
    private readonly Histogram<double> _storageWriteHistogram = instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_WRITE_LATENCY, "ms");
    private readonly Histogram<double> _storageClearHistogram = instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_CLEAR_LATENCY, "ms");
    private readonly Counter<int> _storageReadErrorsCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_READ_ERRORS);
    private readonly Counter<int> _storageWriteErrorsCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_WRITE_ERRORS);
    private readonly Counter<int> _storageClearErrorsCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_CLEAR_ERRORS);

    internal void OnStorageRead(TimeSpan latency, string providerTypeName, string stateName, string stateTypeName)
    {
        if (_storageReadHistogram.Enabled)
        {
            _storageReadHistogram.Record(
                latency.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("provider_type_name", providerTypeName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateTypeName)
                ]);
        }
    }

    internal void OnStorageWrite(TimeSpan latency, string providerTypeName, string stateName, string stateTypeName)
    {
        if (_storageWriteHistogram.Enabled)
        {
            _storageWriteHistogram.Record(
                latency.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("provider_type_name", providerTypeName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateTypeName)
                ]);
        }
    }

    internal void OnStorageReadError(string providerTypeName, string stateName, string stateTypeName)
    {
        if (_storageReadErrorsCounter.Enabled)
        {
            _storageReadErrorsCounter.Add(1,
                [
                    new KeyValuePair<string, object>("provider_type_name", providerTypeName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateTypeName)
                ]);
        }
    }

    internal void OnStorageWriteError(string providerTypeName, string stateName, string stateTypeName)
    {
        if (_storageWriteErrorsCounter.Enabled)
        {
            _storageWriteErrorsCounter.Add(1,
                [
                    new KeyValuePair<string, object>("provider_type_name", providerTypeName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateTypeName)
                ]);
        }
    }

    internal void OnStorageDelete(TimeSpan latency, string providerTypeName, string stateName, string stateTypeName)
    {
        if (_storageClearHistogram.Enabled)
        {
            _storageClearHistogram.Record(latency.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("provider_type_name", providerTypeName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateTypeName)
                ]);
        }
    }

    internal void OnStorageDeleteError(string providerTypeName, string stateName, string stateTypeName)
    {
        if (_storageClearErrorsCounter.Enabled)
        {
            _storageClearErrorsCounter.Add(1,
                [
                    new KeyValuePair<string, object>("provider_type_name", providerTypeName),
                    new KeyValuePair<string, object>("state_name", stateName),
                    new KeyValuePair<string, object>("state_type", stateTypeName)
                ]);
        }
    }
}
