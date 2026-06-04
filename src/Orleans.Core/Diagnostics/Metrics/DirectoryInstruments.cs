using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class DirectoryInstruments
{
    private const string MillisecondsUnit = "ms";
    private const string StatusTagName = "status";
    private const string LocatorTagName = "locator";

    internal const string RegistrationStatusSuccess = "success";
    internal const string RegistrationStatusCanceled = "canceled";
    internal const string RegistrationStatusError = "error";

    private readonly Meter _meter;
    private ImmutableArray<CacheSizeObserverRegistration> _cacheSizeObservers = [];

    internal readonly Counter<int> LookupsLocalIssued;
    internal readonly Counter<int> LookupsLocalSuccesses;

    internal readonly Counter<int> LookupsFullIssued;

    internal readonly Counter<int> LookupsRemoteSent;
    internal readonly Counter<int> LookupsRemoteReceived;

    internal readonly Counter<int> LookupsLocalDirectoryIssued;
    internal readonly Counter<int> LookupsLocalDirectorySuccesses;

    internal readonly Counter<int> LookupsCacheIssued;
    internal readonly Counter<int> LookupsCacheSuccesses;
    internal readonly Counter<int> ValidationsCacheReceived;

    internal readonly Counter<int> SnapshotTransferCount;
    internal readonly Histogram<long> SnapshotTransferDuration;
    internal readonly Counter<long> RangeRecoveryCount;
    internal readonly Histogram<long> RangeRecoveryDuration;
    internal readonly Histogram<long> RangeLockHeldDuration;

    private ObservableGauge<int>? _directoryPartitionSize;
    internal void RegisterDirectoryPartitionSizeObserve(Func<int> observeValue)
    {
        _directoryPartitionSize = _meter.CreateObservableGauge<int>(InstrumentNames.DIRECTORY_PARTITION_SIZE, observeValue);
    }

    private readonly ObservableGauge<int> _cacheSize;
    internal IDisposable RegisterCacheSizeObserve(Func<int> observeValue)
    {
        ArgumentNullException.ThrowIfNull(observeValue);

        var registration = new CacheSizeObserverRegistration(this, observeValue);
        ImmutableInterlocked.Update(ref _cacheSizeObservers, static (observers, registration) => observers.Add(registration), registration);
        return registration;
    }

    private ObservableGauge<int>? _ringSize;
    internal void RegisterRingSizeObserve(Func<int> observeValue)
    {
        _ringSize = _meter.CreateObservableGauge<int>(InstrumentNames.DIRECTORY_RING_RINGSIZE, observeValue);
    }

    private ObservableGauge<long>? _myPortionRingDistance;
    internal void RegisterMyPortionRingDistanceObserve(Func<long> observeValue)
    {
        _myPortionRingDistance = _meter.CreateObservableGauge<long>(InstrumentNames.DIRECTORY_RING_MYPORTION_RINGDISTANCE, observeValue);
    }

    private ObservableGauge<float>? _myPortionRingPercentage;
    internal void RegisterMyPortionRingPercentageObserve(Func<float> observeValue)
    {
        _myPortionRingPercentage = _meter.CreateObservableGauge(InstrumentNames.DIRECTORY_RING_MYPORTION_RINGPERCENTAGE, observeValue);
    }

    private ObservableGauge<float>? _myPortionAverageRingPercentage;
    internal void RegisterMyPortionAverageRingPercentageObserve(Func<float> observeValue)
    {
        _myPortionAverageRingPercentage = _meter.CreateObservableGauge(InstrumentNames.DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE, observeValue);
    }

    internal readonly Counter<int> RegistrationsSingleActIssued;
    internal readonly Counter<int> RegistrationsSingleActLocal;
    internal readonly Counter<int> RegistrationsSingleActRemoteSent;
    internal readonly Counter<int> RegistrationsSingleActRemoteReceived;
    private readonly Counter<int> _registrations;
    private readonly Histogram<double> _registrationDuration;
    internal bool RegistrationMetricsEnabled => _registrations.Enabled || _registrationDuration.Enabled;
    internal readonly Counter<int> UnregistrationsIssued;
    internal readonly Counter<int> UnregistrationsLocal;
    internal readonly Counter<int> UnregistrationsRemoteSent;
    internal readonly Counter<int> UnregistrationsRemoteReceived;
    internal readonly Counter<int> UnregistrationsManyIssued;
    internal readonly Counter<int> UnregistrationsManyRemoteSent;
    internal readonly Counter<int> UnregistrationsManyRemoteReceived;

    public DirectoryInstruments(OrleansInstruments instruments)
    {
        _meter = instruments.Meter;
        LookupsLocalIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED);
        LookupsLocalSuccesses = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES);
        LookupsFullIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_FULL_ISSUED);
        LookupsRemoteSent = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_REMOTE_SENT);
        LookupsRemoteReceived = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_REMOTE_RECEIVED);
        LookupsLocalDirectoryIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED);
        LookupsLocalDirectorySuccesses = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES);
        LookupsCacheIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_CACHE_ISSUED);
        LookupsCacheSuccesses = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_LOOKUPS_CACHE_SUCCESSES);
        ValidationsCacheReceived = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_VALIDATIONS_CACHE_RECEIVED);
        SnapshotTransferCount = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_RANGE_SNAPSHOT_TRANSFER_COUNT);
        SnapshotTransferDuration = _meter.CreateHistogram<long>(InstrumentNames.DIRECTORY_RANGE_SNAPSHOT_TRANSFER_DURATION);
        RangeRecoveryCount = _meter.CreateCounter<long>(InstrumentNames.DIRECTORY_RANGE_RECOVERY_COUNT);
        RangeRecoveryDuration = _meter.CreateHistogram<long>(InstrumentNames.DIRECTORY_RANGE_RECOVERY_DURATION);
        RangeLockHeldDuration = _meter.CreateHistogram<long>(InstrumentNames.DIRECTORY_RANGE_LOCK_HELD_DURATION);
        _cacheSize = _meter.CreateObservableGauge<int>(InstrumentNames.DIRECTORY_CACHE_SIZE, ObserveCacheSize);
        RegistrationsSingleActIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED);
        RegistrationsSingleActLocal = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL);
        RegistrationsSingleActRemoteSent = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT);
        RegistrationsSingleActRemoteReceived = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED);
        _registrations = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_REGISTRATIONS);
        _registrationDuration = _meter.CreateHistogram<double>(InstrumentNames.DIRECTORY_REGISTRATION_DURATION, MillisecondsUnit);
        UnregistrationsIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_ISSUED);
        UnregistrationsLocal = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_LOCAL);
        UnregistrationsRemoteSent = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_REMOTE_SENT);
        UnregistrationsRemoteReceived = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED);
        UnregistrationsManyIssued = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_MANY_ISSUED);
        UnregistrationsManyRemoteSent = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT);
        UnregistrationsManyRemoteReceived = _meter.CreateCounter<int>(InstrumentNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED);
    }

    private int ObserveCacheSize()
    {
        var result = 0;
        foreach (var observer in _cacheSizeObservers)
        {
            result += observer.Observe();
        }

        return result;
    }

    private sealed class CacheSizeObserverRegistration : IDisposable
    {
        private readonly DirectoryInstruments _owner;
        private Func<int>? _observeValue;

        public CacheSizeObserverRegistration(DirectoryInstruments owner, Func<int> observeValue)
        {
            _owner = owner;
            _observeValue = observeValue;
        }

        public int Observe()
        {
            var observer = Volatile.Read(ref _observeValue);
            return observer is null ? 0 : observer();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _observeValue, null) is not null)
            {
                ImmutableInterlocked.Update(ref _owner._cacheSizeObservers, static (observers, registration) => observers.Remove(registration), this);
            }
        }
    }

    internal void OnRegistrationCompleted(TimeSpan latency, string locator, string status)
    {
        if (!RegistrationMetricsEnabled)
        {
            return;
        }

        var tags = CreateRegistrationTags(locator, status);
        if (_registrations.Enabled)
        {
            _registrations.Add(1, tags);
        }

        if (_registrationDuration.Enabled)
        {
            _registrationDuration.Record(Math.Max(0, latency.TotalMilliseconds), tags);
        }
    }

    private static KeyValuePair<string, object?>[] CreateRegistrationTags(string locator, string status) =>
        [
            new(LocatorTagName, locator),
            new(StatusTagName, status)
        ];
}
