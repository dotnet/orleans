using System;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Runtime;

public class CatalogInstrumentsTests
{
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void ActivationLifecycleLatencyMetrics_AreHistograms()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new CatalogInstruments(new OrleansInstruments(meterFactory));

        Instrument activationLatencyInstrument = null!;
        Instrument deactivationLatencyInstrument = null!;
        var activationLatencyMeasurement = 0d;
        var deactivationLatencyMeasurement = 0d;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name is InstrumentNames.CATALOG_ACTIVATION_LATENCY or InstrumentNames.CATALOG_DEACTIVATION_LATENCY)
            {
                meterListener.EnableMeasurementEvents(instrument);
                if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_LATENCY)
                {
                    activationLatencyInstrument = instrument;
                }
                else
                {
                    deactivationLatencyInstrument = instrument;
                }
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_LATENCY)
            {
                activationLatencyMeasurement = measurement;
            }
            else if (instrument.Name == InstrumentNames.CATALOG_DEACTIVATION_LATENCY)
            {
                deactivationLatencyMeasurement = measurement;
            }
        });

        listener.Start();

        instruments.OnActivationCompleted(TimeSpan.FromMilliseconds(12), CatalogInstruments.ActivationStatusSuccess, usesDirectory: true);
        instruments.OnDeactivationCompleted(TimeSpan.FromMilliseconds(34), CatalogInstruments.DeactivationViaCollection);

        Assert.IsType<Histogram<double>>(activationLatencyInstrument);
        Assert.IsType<Histogram<double>>(deactivationLatencyInstrument);
        Assert.Equal("ms", activationLatencyInstrument.Unit);
        Assert.Equal("ms", deactivationLatencyInstrument.Unit);
        Assert.Equal(12, activationLatencyMeasurement);
        Assert.Equal(34, deactivationLatencyMeasurement);
    }
}
