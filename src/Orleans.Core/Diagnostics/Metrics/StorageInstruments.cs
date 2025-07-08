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

    internal static void OnStorageRead(TimeSpan latency, System.Diagnostics.TagList tags)
    {
        StorageReadHistogram.Record(latency.TotalMilliseconds, tags);
    }

    internal static void OnStorageWrite(TimeSpan latency, System.Diagnostics.TagList tags)
    {
        StorageWriteHistogram.Record(latency.TotalMilliseconds, tags);
    }

    internal static void OnStorageReadError(System.Diagnostics.TagList tags)
    {
        StorageReadErrorsCounter.Add(1, tags);
    }

    internal static void OnStorageWriteError(System.Diagnostics.TagList tags)
    {
        StorageWriteErrorsCounter.Add(1, tags);
    }

    internal static void OnStorageDelete(TimeSpan latency, System.Diagnostics.TagList tags)
    {
        StorageClearHistogram.Record(latency.TotalMilliseconds, tags);
    }

    internal static void OnStorageDeleteError(System.Diagnostics.TagList tags)
    {
        StorageClearErrorsCounter.Add(1, tags);
    }
}
