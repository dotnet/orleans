using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class DirectoryInstruments
{
    internal static Counter<int> LookupsLocalIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED);
    internal static Counter<int> LookupsLocalSuccesses = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES);

    internal static Counter<int> LookupsFullIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_FULL_ISSUED);

    internal static Counter<int> LookupsRemoteSent = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_REMOTE_SENT);
    internal static Counter<int> LookupsRemoteReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_REMOTE_RECEIVED);

    internal static Counter<int> LookupsLocalDirectoryIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED);
    internal static Counter<int> LookupsLocalDirectorySuccesses = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES);

    internal static Counter<int> LookupsCacheIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_CACHE_ISSUED);
    internal static Counter<int> LookupsCacheSuccesses = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_CACHE_SUCCESSES);
    internal static Counter<int> ValidationsCacheSent = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_VALIDATIONS_CACHE_SENT);
    internal static Counter<int> ValidationsCacheReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_VALIDATIONS_CACHE_RECEIVED);

    internal static ObservableGauge<int> DirectoryPartitionSize;
    internal static void RegisterDirectoryPartitionSizeObserve(Func<int> observeValue)
    {
        DirectoryPartitionSize = Instruments.Meter.CreateObservableGauge<int>(InstrumentNames.DIRECTORY_PARTITION_SIZE, observeValue);
    }

    internal static ObservableGauge<int> CacheSize;
    internal static void RegisterCacheSizeObserve(Func<int> observeValue)
    {
        CacheSize = Instruments.Meter.CreateObservableGauge<int>(InstrumentNames.DIRECTORY_CACHE_SIZE, observeValue);
    }

    internal static ObservableGauge<int> RingSize;
    internal static void RegisterRingSizeObserve(Func<int> observeValue)
    {
        RingSize = Instruments.Meter.CreateObservableGauge<int>(InstrumentNames.DIRECTORY_RING_RINGSIZE, observeValue);
    }

    internal static ObservableGauge<long> MyPortionRingDistance;
    internal static void RegisterMyPortionRingDistanceObserve(Func<long> observeValue)
    {
        MyPortionRingDistance = Instruments.Meter.CreateObservableGauge<long>(InstrumentNames.DIRECTORY_RING_MYPORTION_RINGDISTANCE, observeValue);
    }

    internal static ObservableGauge<float> MyPortionRingPercentage;
    internal static void RegisterMyPortionRingPercentageObserve(Func<float> observeValue)
    {
        MyPortionRingPercentage = Instruments.Meter.CreateObservableGauge(InstrumentNames.DIRECTORY_RING_MYPORTION_RINGPERCENTAGE, observeValue);
    }

    internal static ObservableGauge<float> MyPortionAverageRingPercentage;
    internal static void RegisterMyPortionAverageRingPercentageObserve(Func<float> observeValue)
    {
        MyPortionAverageRingPercentage = Instruments.Meter.CreateObservableGauge(InstrumentNames.DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE, observeValue);
    }

    internal static Counter<int> RegistrationsSingleActIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED);
    internal static Counter<int> RegistrationsSingleActLocal = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL);
    internal static Counter<int> RegistrationsSingleActRemoteSent = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT);
    internal static Counter<int> RegistrationsSingleActRemoteReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED);
    internal static Counter<int> UnregistrationsIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_ISSUED);
    internal static Counter<int> UnregistrationsLocal = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_LOCAL);
    internal static Counter<int> UnregistrationsRemoteSent = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_REMOTE_SENT);
    internal static Counter<int> UnregistrationsRemoteReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED);
    internal static Counter<int> UnregistrationsManyIssued = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_MANY_ISSUED);
    internal static Counter<int> UnregistrationsManyRemoteSent = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT);
    internal static Counter<int> UnregistrationsManyRemoteReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED);
}
