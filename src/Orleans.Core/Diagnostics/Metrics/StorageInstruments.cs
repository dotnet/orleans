using System;
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

    internal static void OnStorageRead(TimeSpan latency)
    {
        StorageReadHistogram.Record(latency.TotalMilliseconds);
    }
    internal static void OnStorageWrite(TimeSpan latency)
    {
        StorageWriteHistogram.Record(latency.TotalMilliseconds);
    }
    internal static void OnStorageReadError()
    {
        StorageReadErrorsCounter.Add(1);
    }
    internal static void OnStorageWriteError()
    {
        StorageWriteErrorsCounter.Add(1);
    }
    internal static void OnStorageDelete(TimeSpan latency)
    {
        StorageClearHistogram.Record(latency.TotalMilliseconds);
    }
    internal static void OnStorageDeleteError()
    {
        StorageClearErrorsCounter.Add(1);
    }
}
