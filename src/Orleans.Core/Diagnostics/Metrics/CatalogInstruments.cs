using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using MetricsTagList = System.Diagnostics.TagList;

namespace Orleans.Runtime;

internal sealed class CatalogInstruments(OrleansInstruments instruments)
{
    private readonly ConcurrentDictionary<GrainType, GrainTypeMetrics> _grainTypes = new();
    private const string MillisecondsUnit = "ms";
    private const string StatusTagName = "status";
    private const string DirectoryTagName = "directory";
    private const string ViaTagName = "via";
    private const string DirectoryEnabled = "enabled";
    private const string DirectoryDisabled = "disabled";

    internal const string ActivationStatusSuccess = "success";
    internal const string ActivationStatusCanceled = "canceled";
    internal const string ActivationStatusDirectoryError = "directory_error";
    internal const string ActivationStatusDuplicate = "duplicate";
    internal const string ActivationStatusError = "error";

    internal const string DeactivationViaCollection = "collection";
    internal const string DeactivationViaDeactivateOnIdle = "deactivateOnIdle";
    internal const string DeactivationViaDeactivateStuckActivation = "deactivateStuckActivation";
    internal const string DeactivationViaMigration = "migration";
    internal const string DeactivationViaUnknown = "unknown";

    internal struct ActivationMetricTracker
    {
        private readonly CatalogInstruments? _instruments;
        private readonly ValueStopwatch _stopwatch;
        private readonly bool _usesDirectory;
        private readonly string? _grainType;
        private readonly bool _isGrainTypeKnown;
        private string? _status;

        private ActivationMetricTracker(CatalogInstruments instruments, ValueStopwatch stopwatch, bool usesDirectory, string grainType, bool isGrainTypeKnown, string status)
        {
            _instruments = instruments;
            _stopwatch = stopwatch;
            _usesDirectory = usesDirectory;
            _grainType = grainType;
            _isGrainTypeKnown = isGrainTypeKnown;
            _status = status;
        }

        public static ActivationMetricTracker Start(CatalogInstruments instruments, bool usesDirectory, string grainType, bool isGrainTypeKnown = true)
        {
            return instruments.ActivationLatencyEnabled
                ? new(instruments, ValueStopwatch.StartNew(), usesDirectory, grainType, isGrainTypeKnown, ActivationStatusError)
                : default;
        }

        public void Succeeded() => SetStatus(ActivationStatusSuccess);

        public void Failed(bool cancellationRequested) => SetStatus(cancellationRequested
            ? ActivationStatusCanceled
            : ActivationStatusError);

        public void DirectoryRegistrationFailed(Exception? exception, bool cancellationRequested) => SetStatus(exception is null
            ? ActivationStatusDuplicate
            : cancellationRequested
                ? ActivationStatusCanceled
                : ActivationStatusDirectoryError);

        public void Canceled() => SetStatus(ActivationStatusCanceled);

        public void Record()
        {
            if (_status is null || _instruments is null)
            {
                return;
            }

            var status = _status;
            _status = null;
            _instruments.OnActivationCompleted(_stopwatch.Elapsed, status, _usesDirectory, _grainType!, _isGrainTypeKnown);
        }

        private void SetStatus(string status)
        {
            if (_status is not null)
            {
                _status = status;
            }
        }
    }

    internal struct DeactivationMetricTracker
    {
        private readonly CatalogInstruments? _instruments;
        private readonly ValueStopwatch _stopwatch;
        private readonly string? _via;
        private readonly string? _grainType;
        private readonly bool _isGrainTypeKnown;
        private bool _recorded;

        private DeactivationMetricTracker(CatalogInstruments instruments, ValueStopwatch stopwatch, string via, string grainType, bool isGrainTypeKnown, bool recorded)
        {
            _instruments = instruments;
            _stopwatch = stopwatch;
            _via = via;
            _grainType = grainType;
            _isGrainTypeKnown = isGrainTypeKnown;
            _recorded = recorded;
        }

        public static DeactivationMetricTracker Start(CatalogInstruments instruments, string grainType, bool isGrainTypeKnown = true)
        {
            return instruments.DeactivationLatencyEnabled
                ? new(instruments, ValueStopwatch.StartNew(), DeactivationViaUnknown, grainType, isGrainTypeKnown, recorded: false)
                : default;
        }

        public DeactivationMetricTracker Collection() => WithVia(DeactivationViaCollection);

        public DeactivationMetricTracker DeactivateOnIdle() => WithVia(DeactivationViaDeactivateOnIdle);

        public DeactivationMetricTracker DeactivateStuckActivation() => WithVia(DeactivationViaDeactivateStuckActivation);

        public DeactivationMetricTracker Migration() => WithVia(DeactivationViaMigration);

        public DeactivationMetricTracker Record()
        {
            if (_via is null || _recorded || _instruments is null)
            {
                return this;
            }

            _recorded = true;
            _instruments.OnDeactivationCompleted(_stopwatch.Elapsed, _via, _grainType!, _isGrainTypeKnown);
            return this;
        }

        public void RecordIfNeeded()
        {
            Record();
        }

        private DeactivationMetricTracker WithVia(string via) => _via is null ? this : new(_instruments!, _stopwatch, via, _grainType!, _isGrainTypeKnown, _recorded);
    }

    private readonly Counter<int> _activationFailedToActivate = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE);

    private readonly Counter<int> _activationCollections = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS);

    private readonly Counter<int> _activationShutdown = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN);

    internal void ActivationShutdownViaCollection(string grainType, bool isGrainTypeKnown = true) => OnActivationShutdown(DeactivationViaCollection, grainType, isGrainTypeKnown);
    internal void ActivationShutdownViaDeactivateOnIdle(string grainType, bool isGrainTypeKnown = true) => OnActivationShutdown(DeactivationViaDeactivateOnIdle, grainType, isGrainTypeKnown);
    internal void ActivationShutdownViaMigration(string grainType, bool isGrainTypeKnown = true) => OnActivationShutdown(DeactivationViaMigration, grainType, isGrainTypeKnown);
    internal void ActivationShutdownViaDeactivateStuckActivation(string grainType, bool isGrainTypeKnown = true) => OnActivationShutdown(DeactivationViaDeactivateStuckActivation, grainType, isGrainTypeKnown);

    private readonly Histogram<double> _deactivationLatency = instruments.Meter.CreateHistogram<double>(InstrumentNames.CATALOG_DEACTIVATION_LATENCY, MillisecondsUnit);
    internal bool DeactivationLatencyEnabled => _deactivationLatency.Enabled;

    internal void OnDeactivationCompleted(TimeSpan latency, string via, string grainType, bool isGrainTypeKnown = true)
    {
        if (_deactivationLatency.Enabled)
        {
            var tags = new MetricsTagList { { ViaTagName, via } };
            GrainTypeMetrics.AddTags(ref tags, grainType, isGrainTypeKnown);
            _deactivationLatency.Record(latency.TotalMilliseconds, tags);
        }
    }

    private readonly Counter<int> _nonExistentActivations = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS);
    internal bool NonExistentActivationsEnabled => _nonExistentActivations.Enabled;

    private readonly Counter<int> _activationConcurrentRegistrationAttempts = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS);

    private readonly Counter<int> _activationsCreated = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CREATED);
    private readonly Counter<int> _activationsDestroyed = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_DESTROYED);
    private readonly Histogram<double> _activationLatency = instruments.Meter.CreateHistogram<double>(InstrumentNames.CATALOG_ACTIVATION_LATENCY, MillisecondsUnit);
    internal bool ActivationLatencyEnabled => _activationLatency.Enabled;

    private ObservableGauge<int>? _activationCount;

    internal void RegisterActivationCountObserve()
    {
        _activationCount = instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_COUNT, ObserveActivationCounts);
    }

    private ObservableGauge<int>? _activationWorkingSet;
    internal void RegisterActivationWorkingSetObserve()
    {
        _activationWorkingSet = instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET, ObserveWorkingSetCounts);
    }

    internal GrainTypeMetrics GetGrainTypeMetrics(GrainType grainType)
    {
        if (_grainTypes.TryGetValue(grainType, out var result))
        {
            return result;
        }

        // Serialize registration so even concurrent first activations share one string.
        lock (_grainTypes)
        {
            if (!_grainTypes.TryGetValue(grainType, out result))
            {
                result = new(grainType);
                _grainTypes[grainType] = result;
            }
        }

        return result;
    }

    internal bool TryGetGrainTypeMetrics(GrainType grainType, [NotNullWhen(true)] out GrainTypeMetrics? result) =>
        _grainTypes.TryGetValue(grainType, out result);

    private IEnumerable<Measurement<int>> ObserveActivationCounts()
    {
        foreach (var entry in _grainTypes)
        {
            yield return entry.Value.ActivationCount;
        }
    }

    private IEnumerable<Measurement<int>> ObserveWorkingSetCounts()
    {
        foreach (var entry in _grainTypes)
        {
            yield return entry.Value.WorkingSetCount;
        }
    }

    internal void OnActivationCompleted(TimeSpan latency, string status, bool usesDirectory, string grainType, bool isGrainTypeKnown = true)
    {
        if (_activationLatency.Enabled)
        {
            var tags = new MetricsTagList
            {
                { StatusTagName, status },
                { DirectoryTagName, usesDirectory ? DirectoryEnabled : DirectoryDisabled }
            };
            GrainTypeMetrics.AddTags(ref tags, grainType, isGrainTypeKnown);
            _activationLatency.Record(Math.Max(0, latency.TotalMilliseconds), tags);
        }
    }

    internal void OnActivationFailedToActivate(string grainType, bool isGrainTypeKnown = true)
    {
        if (_activationFailedToActivate.Enabled)
        {
            _activationFailedToActivate.Add(1, GrainTypeMetrics.CreateTags(grainType, isGrainTypeKnown));
        }
    }

    internal void OnActivationConcurrentRegistrationAttempt(string grainType, bool isGrainTypeKnown = true)
    {
        if (_activationConcurrentRegistrationAttempts.Enabled)
        {
            _activationConcurrentRegistrationAttempts.Add(1, GrainTypeMetrics.CreateTags(grainType, isGrainTypeKnown));
        }
    }

    internal void OnActivationCollected()
    {
        _activationCollections.Add(1);
    }

    internal void OnActivationCreated(string grainType, bool isGrainTypeKnown = true)
    {
        if (_activationsCreated.Enabled)
        {
            _activationsCreated.Add(1, GrainTypeMetrics.CreateTags(grainType, isGrainTypeKnown));
        }
    }

    internal void OnActivationDestroyed(string grainType, bool isGrainTypeKnown = true)
    {
        if (_activationsDestroyed.Enabled)
        {
            _activationsDestroyed.Add(1, GrainTypeMetrics.CreateTags(grainType, isGrainTypeKnown));
        }
    }

    internal void OnNonExistentActivation(string grainType, bool isGrainTypeKnown = true)
    {
        if (_nonExistentActivations.Enabled)
        {
            _nonExistentActivations.Add(1, GrainTypeMetrics.CreateTags(grainType, isGrainTypeKnown));
        }
    }

    private void OnActivationShutdown(string via, string grainType, bool isGrainTypeKnown)
    {
        if (_activationShutdown.Enabled)
        {
            var tags = new MetricsTagList { { ViaTagName, via } };
            GrainTypeMetrics.AddTags(ref tags, grainType, isGrainTypeKnown);
            _activationShutdown.Add(1, tags);
        }
    }
}
