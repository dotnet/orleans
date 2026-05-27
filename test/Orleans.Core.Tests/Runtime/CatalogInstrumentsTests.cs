using System;
using System.Diagnostics.Metrics;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Runtime;

public class CatalogInstrumentsTests
{
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void ActivationLifecycleLatencyMetrics_AreHistograms()
    {
        Instrument activationDurationInstrument = null!;
        Instrument deactivationLatencyInstrument = null!;
        var activationDurationMeasurement = 0d;
        var deactivationLatencyMeasurement = 0d;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name is InstrumentNames.CATALOG_ACTIVATION_DURATION or InstrumentNames.CATALOG_DEACTIVATION_LATENCY)
            {
                meterListener.EnableMeasurementEvents(instrument);
                if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_DURATION)
                {
                    activationDurationInstrument = instrument;
                }
                else
                {
                    deactivationLatencyInstrument = instrument;
                }
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_DURATION)
            {
                activationDurationMeasurement = measurement;
            }
            else if (instrument.Name == InstrumentNames.CATALOG_DEACTIVATION_LATENCY)
            {
                deactivationLatencyMeasurement = measurement;
            }
        });

        listener.Start();

        CatalogInstruments.OnActivationCompleted(TimeSpan.FromMilliseconds(12), CatalogInstruments.ActivationStatusSuccess, usesDirectory: true);
        CatalogInstruments.OnDeactivationCompleted(TimeSpan.FromMilliseconds(34), CatalogInstruments.DeactivationViaCollection);

        Assert.IsType<Histogram<double>>(activationDurationInstrument);
        Assert.IsType<Histogram<double>>(deactivationLatencyInstrument);
        Assert.Equal("ms", activationDurationInstrument.Unit);
        Assert.Equal("ms", deactivationLatencyInstrument.Unit);
        Assert.Equal(12, activationDurationMeasurement);
        Assert.Equal(34, deactivationLatencyMeasurement);
    }
}
