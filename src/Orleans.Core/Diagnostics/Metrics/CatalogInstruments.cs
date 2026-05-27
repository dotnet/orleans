using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class CatalogInstruments
{
    private const string MillisecondsUnit = "ms";
    private const string StatusTagName = "status";
    private const string DirectoryTagName = "directory";
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

    internal static Counter<int> ActivationFailedToActivate = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE);

    internal static Counter<int> ActivationCollections = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS);

    internal static Counter<int> ActivationShutdown = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN);

    internal static void ActivationShutdownViaCollection() => ActivationShutdown.Add(1, new KeyValuePair<string, object?>("via", DeactivationViaCollection));
    internal static void ActivationShutdownViaDeactivateOnIdle() => ActivationShutdown.Add(1, new KeyValuePair<string, object?>("via", DeactivationViaDeactivateOnIdle));
    internal static void ActivationShutdownViaMigration() => ActivationShutdown.Add(1, new KeyValuePair<string, object?>("via", DeactivationViaMigration));
    internal static void ActivationShutdownViaDeactivateStuckActivation() => ActivationShutdown.Add(1, new KeyValuePair<string, object?>("via", DeactivationViaDeactivateStuckActivation));

    internal static Histogram<double> DeactivationLatency = Instruments.Meter.CreateHistogram<double>(InstrumentNames.CATALOG_DEACTIVATION_LATENCY, MillisecondsUnit);

    internal static void OnDeactivationCompleted(TimeSpan latency, string via)
    {
        if (DeactivationLatency.Enabled)
        {
            DeactivationLatency.Record(latency.TotalMilliseconds, new KeyValuePair<string, object?>("via", via));
        }
    }

    internal static Counter<int> NonExistentActivations = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS);

    internal static Counter<int> ActivationConcurrentRegistrationAttempts = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS);

    internal static readonly Counter<int> ActivationsCreated = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CREATED);
    internal static readonly Counter<int> ActivationsDestroyed = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_DESTROYED);
    private static readonly Histogram<double> ActivationDuration = Instruments.Meter.CreateHistogram<double>(InstrumentNames.CATALOG_ACTIVATION_DURATION, MillisecondsUnit);

    internal static ObservableGauge<int>? ActivationCount;

    internal static void RegisterActivationCountObserve(Func<int> observeValue)
    {
        ActivationCount = Instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_COUNT, observeValue);
    }

    internal static ObservableGauge<int>? ActivationWorkingSet;
    internal static void RegisterActivationWorkingSetObserve(Func<int> observeValue)
    {
        ActivationWorkingSet = Instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_WORKING_SET, observeValue);
    }

    internal static void OnActivationCompleted(TimeSpan latency, string status, bool usesDirectory)
    {
        if (ActivationDuration.Enabled)
        {
            ActivationDuration.Record(
                Math.Max(0, latency.TotalMilliseconds),
                [
                    new KeyValuePair<string, object?>(StatusTagName, status),
                    new KeyValuePair<string, object?>(DirectoryTagName, usesDirectory ? DirectoryEnabled : DirectoryDisabled)
                ]);
        }
    }
}
