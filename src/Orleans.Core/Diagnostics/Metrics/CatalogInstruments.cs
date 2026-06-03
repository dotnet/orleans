using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class CatalogInstruments(OrleansInstruments instruments)
{
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
        private readonly CatalogInstruments _instruments;
        private readonly ValueStopwatch _stopwatch;
        private readonly bool _usesDirectory;
        private string? _status;

        private ActivationMetricTracker(CatalogInstruments instruments, ValueStopwatch stopwatch, bool usesDirectory, string status)
        {
            _instruments = instruments;
            _stopwatch = stopwatch;
            _usesDirectory = usesDirectory;
            _status = status;
        }

        public static ActivationMetricTracker Start(CatalogInstruments instruments, bool usesDirectory)
        {
            return instruments.ActivationLatencyEnabled
                ? new(instruments, ValueStopwatch.StartNew(), usesDirectory, ActivationStatusError)
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

            _instruments.OnActivationCompleted(_stopwatch.Elapsed, _status, _usesDirectory);
        }

        private void SetStatus(string status)
        {
            if (_status is not null)
            {
                _status = status;
            }
        }
    }

    internal readonly struct DeactivationMetricTracker
    {
        private readonly CatalogInstruments _instruments;
        private readonly ValueStopwatch _stopwatch;
        private readonly string? _via;
        private readonly bool _recorded;

        private DeactivationMetricTracker(CatalogInstruments instruments, ValueStopwatch stopwatch, string via, bool recorded)
        {
            _instruments = instruments;
            _stopwatch = stopwatch;
            _via = via;
            _recorded = recorded;
        }

        public static DeactivationMetricTracker Start(CatalogInstruments instruments)
        {
            return instruments.DeactivationLatencyEnabled
                ? new(instruments, ValueStopwatch.StartNew(), DeactivationViaUnknown, recorded: false)
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

            _instruments.OnDeactivationCompleted(_stopwatch.Elapsed, _via);
            return new(_instruments, _stopwatch, _via, recorded: true);
        }

        public void RecordIfNeeded()
        {
            if (_via is null || _recorded || _instruments is null)
            {
                return;
            }

            _instruments.OnDeactivationCompleted(_stopwatch.Elapsed, _via);
        }

        private DeactivationMetricTracker WithVia(string via) => _via is null ? this : new(_instruments, _stopwatch, via, _recorded);
    }

    private readonly Counter<int> _activationFailedToActivate = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE);

    private readonly Counter<int> _activationCollections = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS);

    private readonly Counter<int> _activationShutdown = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN);

    internal void ActivationShutdownViaCollection() => OnActivationShutdown(DeactivationViaCollection);
    internal void ActivationShutdownViaDeactivateOnIdle() => OnActivationShutdown(DeactivationViaDeactivateOnIdle);
    internal void ActivationShutdownViaMigration() => OnActivationShutdown(DeactivationViaMigration);
    internal void ActivationShutdownViaDeactivateStuckActivation() => OnActivationShutdown(DeactivationViaDeactivateStuckActivation);

    private readonly Histogram<double> _deactivationLatency = instruments.Meter.CreateHistogram<double>(InstrumentNames.CATALOG_DEACTIVATION_LATENCY, MillisecondsUnit);
    internal bool DeactivationLatencyEnabled => _deactivationLatency.Enabled;

    internal void OnDeactivationCompleted(TimeSpan latency, string via)
    {
        if (_deactivationLatency.Enabled)
        {
            _deactivationLatency.Record(latency.TotalMilliseconds, new KeyValuePair<string, object?>(ViaTagName, via));
        }
    }

    private readonly Counter<int> _nonExistentActivations = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS);

    private readonly Counter<int> _activationConcurrentRegistrationAttempts = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS);

    private readonly Counter<int> _activationsCreated = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CREATED);
    private readonly Counter<int> _activationsDestroyed = instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_DESTROYED);
    private readonly Histogram<double> _activationLatency = instruments.Meter.CreateHistogram<double>(InstrumentNames.CATALOG_ACTIVATION_LATENCY, MillisecondsUnit);
    internal bool ActivationLatencyEnabled => _activationLatency.Enabled;

    private ObservableGauge<int>? _activationCount;

    internal void RegisterActivationCountObserve(Func<int> observeValue)
    {
        _activationCount = instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_COUNT, observeValue);
    }

    private ObservableGauge<int>? _activationWorkingSet;
    internal void RegisterActivationWorkingSetObserve(Func<int> observeValue)
    {
        _activationWorkingSet = instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET, observeValue);
    }

    internal void OnActivationCompleted(TimeSpan latency, string status, bool usesDirectory)
    {
        if (_activationLatency.Enabled)
        {
            _activationLatency.Record(
                Math.Max(0, latency.TotalMilliseconds),
                [
                    new KeyValuePair<string, object?>(StatusTagName, status),
                    new KeyValuePair<string, object?>(DirectoryTagName, usesDirectory ? DirectoryEnabled : DirectoryDisabled)
                ]);
        }
    }

    internal void OnActivationFailedToActivate()
    {
        if (_activationFailedToActivate.Enabled)
        {
            _activationFailedToActivate.Add(1);
        }
    }

    internal void OnActivationConcurrentRegistrationAttempt()
    {
        if (_activationConcurrentRegistrationAttempts.Enabled)
        {
            _activationConcurrentRegistrationAttempts.Add(1);
        }
    }

    internal void OnActivationCollected()
    {
        _activationCollections.Add(1);
    }

    internal void OnActivationCreated()
    {
        _activationsCreated.Add(1);
    }

    internal void OnActivationDestroyed()
    {
        _activationsDestroyed.Add(1);
    }

    internal void OnNonExistentActivation()
    {
        _nonExistentActivations.Add(1);
    }

    private void OnActivationShutdown(string via)
    {
        if (_activationShutdown.Enabled)
        {
            _activationShutdown.Add(1, new KeyValuePair<string, object?>(ViaTagName, via));
        }
    }
}
