using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class CatalogInstruments
{
    internal static Counter<int> ActivationFailedToActivate = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE);

    internal static Counter<int> ActivationCollections = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS);

    internal static Counter<int> ActivationShutdown = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN);

    internal static void ActiviationShutdownViaCollection() => ActivationShutdown.Add(1, new KeyValuePair<string, object>("via", "collection"));
    internal static void ActiviationShutdownViaDeactivateOnIdle() => ActivationShutdown.Add(1, new KeyValuePair<string, object>("via", "deactivateOnIdle"));
    internal static void ActiviationShutdownViaDeactivateStuckActivation() => ActivationShutdown.Add(1, new KeyValuePair<string, object>("via", "deactivateStuckActivation"));

    internal static Counter<int> NonExistentActivations = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS);

    internal static Counter<int> ActivationConcurrentRegistrationAttempts = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS);

    internal static readonly Counter<int> ActivationsCreated = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_CREATED);
    internal static readonly Counter<int> ActivationsDestroyed = Instruments.Meter.CreateCounter<int>(InstrumentNames.CATALOG_ACTIVATION_DESTROYED);

    internal static ObservableGauge<int> ActivationCount;
    internal static void RegisterActivationCountObserve(Func<int> observeValue)
    {
        ActivationCount = Instruments.Meter.CreateObservableGauge(InstrumentNames.CATALOG_ACTIVATION_COUNT, observeValue);
    }
}
