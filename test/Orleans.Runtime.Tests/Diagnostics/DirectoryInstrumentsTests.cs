using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class DirectoryInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void DirectoryInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new DirectoryInstruments(new OrleansInstruments(meterFactory));
        using var localLookupsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED);
        using var cacheSizeCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.DIRECTORY_CACHE_SIZE);
        using var registrationsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.DIRECTORY_REGISTRATIONS);
        using var registrationDurationCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.DIRECTORY_REGISTRATION_DURATION);

        using var cacheRegistration = instruments.RegisterCacheSizeObserve(() => 7);
        instruments.LookupsLocalIssued.Add(1);
        instruments.OnRegistrationCompleted(TimeSpan.FromMilliseconds(12), "dht", DirectoryInstruments.RegistrationStatusSuccess);

        cacheSizeCollector.RecordObservableInstruments();

        Assert.Equal(1, Assert.Single(localLookupsCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(7, Assert.Single(cacheSizeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(registrationsCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(12, Assert.Single(registrationDurationCollector.GetMeasurementSnapshot()).Value);
    }
}
