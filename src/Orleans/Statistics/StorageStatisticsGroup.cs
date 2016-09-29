using System;
using Orleans.Storage;

namespace Orleans.Runtime
{
    internal class StorageStatisticsGroup
    {
        internal static CounterStatistic StorageReadTotal;
        internal static CounterStatistic StorageWriteTotal;
        internal static CounterStatistic StorageActivateTotal;
        internal static CounterStatistic StorageClearTotal;
        internal static CounterStatistic StorageReadErrors;
        internal static CounterStatistic StorageWriteErrors;
        internal static CounterStatistic StorageActivateErrors;
        internal static CounterStatistic StorageClearErrors;
        internal static AverageTimeSpanStatistic StorageReadLatency;
        internal static AverageTimeSpanStatistic StorageWriteLatency;
        internal static AverageTimeSpanStatistic StorageClearLatency;

        internal static void Init()
        {
            StorageReadTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_READ_TOTAL);
            StorageWriteTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_WRITE_TOTAL);
            StorageActivateTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_ACTIVATE_TOTAL);
            StorageReadErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_READ_ERRORS);
            StorageWriteErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_WRITE_ERRORS);
            StorageActivateErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_ACTIVATE_ERRORS);
            StorageReadLatency = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.STORAGE_READ_LATENCY);
            StorageWriteLatency = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.STORAGE_WRITE_LATENCY);
            StorageClearTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_CLEAR_TOTAL);
            StorageClearErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_CLEAR_ERRORS);
            StorageClearLatency = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.STORAGE_CLEAR_LATENCY);
        }

        internal static void OnStorageRead(IStorageProvider storage, string grainType, GrainReference grain, TimeSpan latency)
        {
            StorageReadTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageReadLatency.AddSample(latency);
            }
        }
        internal static void OnStorageWrite(IStorageProvider storage, string grainType, GrainReference grain, TimeSpan latency)
        {
            StorageWriteTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageWriteLatency.AddSample(latency);
            }
        }
        internal static void OnStorageActivate(IStorageProvider storage, string grainType, GrainReference grain, TimeSpan latency)
        {
            StorageActivateTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageReadLatency.AddSample(latency);
            }
        }
        internal static void OnStorageReadError(IStorageProvider storage, string grainType, GrainReference grain)
        {
            StorageReadErrors.Increment();
        }
        internal static void OnStorageWriteError(IStorageProvider storage, string grainType, GrainReference grain)
        {
            StorageWriteErrors.Increment();
        }
        internal static void OnStorageActivateError(IStorageProvider storage, string grainType, GrainReference grain)
        {
            StorageActivateErrors.Increment();
        }
        internal static void OnStorageDelete(IStorageProvider storage, string grainType, GrainReference grain, TimeSpan latency)
        {
            StorageClearTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageClearLatency.AddSample(latency);
            }
        }
        internal static void OnStorageDeleteError(IStorageProvider storage, string grainType, GrainReference grain)
        {
            StorageClearErrors.Increment();
        }
    }
}
