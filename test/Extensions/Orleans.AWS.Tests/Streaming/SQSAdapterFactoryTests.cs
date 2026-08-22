using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using OrleansAWSUtils.Streams;
using TestExtensions;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[TestSuite("BVT")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS")]
public sealed class SQSAdapterFactoryTests
{
    [Fact]
    public void Create_UsesNamedQueueAndCacheOptions()
    {
        const string providerName = "NamedProvider";
        using var serviceProvider = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .AddSerializer()
            .Configure<ClusterOptions>(options =>
            {
                options.ServiceId = "ServiceId";
                options.ClusterId = "ClusterId";
            })
            .Configure<SqsOptions>(providerName, options => options.ConnectionString = "Service=us-east-1")
            .Configure<HashRingStreamQueueMapperOptions>(providerName, options => options.TotalQueueCount = 4)
            .Configure<SimpleQueueCacheOptions>(providerName, options => options.CacheSize = 1234)
            .BuildServiceProvider();

        var factory = SQSAdapterFactory.Create(serviceProvider, providerName);
        var mapper = Assert.IsType<HashRingBasedStreamQueueMapper>(factory.GetStreamQueueMapper());

        Assert.Equal(4, mapper.GetAllQueues().Count());
        Assert.IsType<SimpleQueueAdapterCache>(factory.GetQueueAdapterCache());
    }
}
