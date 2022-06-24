using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal class StorageInstruments
{
    internal static Histogram<double> StorageReadHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_READ_LATENCY);
    internal static Histogram<double> StorageWriteHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_WRITE_LATENCY);
    internal static Histogram<double> StorageClearHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.STORAGE_CLEAR_LATENCY);
    internal static Counter<int> StorageReadErrorsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_READ_ERRORS);
    internal static Counter<int> StorageWriteErrorsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_WRITE_ERRORS);
    internal static Counter<int> StorageClearErrorsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.STORAGE_CLEAR_ERRORS);

    internal static void OnStorageRead(string grainType, GrainReference grain, TimeSpan latency)
    {
        StorageReadHistogram.Record(latency.TotalMilliseconds);
    }
    internal static void OnStorageWrite(string grainType, GrainReference grain, TimeSpan latency)
    {
        StorageWriteHistogram.Record(latency.TotalMilliseconds);
    }
    internal static void OnStorageReadError(string grainType, GrainReference grain)
    {
        StorageReadErrorsCounter.Add(1);
    }
    internal static void OnStorageWriteError(string grainType, GrainReference grain)
    {
        StorageWriteErrorsCounter.Add(1);
    }
    internal static void OnStorageDelete(string grainType, GrainReference grain, TimeSpan latency)
    {
        StorageClearHistogram.Record(latency.TotalMilliseconds);
    }
    internal static void OnStorageDeleteError(string grainType, GrainReference grain)
    {
        StorageClearErrorsCounter.Add(1);
    }
}
