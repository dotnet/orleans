using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.StatisticMonitors;
using Orleans.Streaming.EventHubs.Testing;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class EventHubQueueCacheFactoryTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void CreateCache_UsesDistinctPoolIdsPerFactoryAndReusesIdAcrossPartitions()
    {
        using var services = new ServiceCollection().AddMetrics().AddSerializer().BuildServiceProvider();
        var instruments = new OrleansInstruments(services.GetRequiredService<IMeterFactory>());
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var poolDimensions = new List<EventHubBlockPoolMonitorDimensions>();
        var cacheDimensions = new List<EventHubCacheMonitorDimensions>();
        var firstFactory = CreateFactory();
        var secondFactory = CreateFactory();

        using (firstFactory.CreateCache("0", NoOpCheckpointer.Instance, NullLoggerFactory.Instance))
        {
            using var secondCache = firstFactory.CreateCache("1", NoOpCheckpointer.Instance, NullLoggerFactory.Instance);
        }
        using var replacementCache = firstFactory.CreateCache("0", NoOpCheckpointer.Instance, NullLoggerFactory.Instance);
        using var otherFactoryCache = secondFactory.CreateCache("0", NoOpCheckpointer.Instance, NullLoggerFactory.Instance);

        Assert.Equal(2, poolDimensions.Count);
        var firstPoolId = poolDimensions[0].BlockPoolId;
        var secondPoolId = poolDimensions[1].BlockPoolId;
        Assert.NotEmpty(firstPoolId);
        Assert.NotEmpty(secondPoolId);
        Assert.NotEqual(firstPoolId, secondPoolId);
        Assert.Equal(new[] { firstPoolId, firstPoolId, firstPoolId, secondPoolId }, cacheDimensions.Select(d => d.BlockPoolId));
        Assert.All(poolDimensions, d => Assert.Equal("shared-hub", d.EventHubPath));
        Assert.All(cacheDimensions, d => Assert.Equal("shared-hub", d.EventHubPath));

        EventHubQueueCacheFactory CreateFactory() => new(
            new EventHubStreamCachePressureOptions(),
            new StreamCacheEvictionOptions(),
            new StreamStatisticOptions(),
            adapter,
            new EventHubMonitorAggregationDimensions("shared-hub"),
            instruments,
            cacheMonitorFactory: (dimensions, _) =>
            {
                cacheDimensions.Add(dimensions);
                return new DefaultEventHubCacheMonitor(dimensions, instruments);
            },
            blockPoolMonitorFactory: (dimensions, _) =>
            {
                poolDimensions.Add(dimensions);
                return new DefaultEventHubBlockPoolMonitor(dimensions, instruments);
            });
    }
}
