using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Journaling.Json;
using Orleans.Hosting;
using Orleans.Journaling;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

#pragma warning disable ORLEANSEXP005

[TestCategory("Azure"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("DurableJobs")]
public class AzureStorageDurableJobsConfigurationTests
{
    [Fact]
    public void DurableJobsTestContainerNamesAreScopedByServiceId()
    {
        var first = AzureStorageBlobDurableJobsTests.GetContainerName("service-a");
        var second = AzureStorageBlobDurableJobsTests.GetContainerName("service-b");

        Assert.Equal("durablejobs-tests-service-a", first);
        Assert.Equal("durablejobs-tests-service-b", second);
        Assert.Equal("durablejobs-tests-service-a", AzureStorageBlobDurableJobsTests.GetContainerName("Service-A"));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void UseAzureBlobDurableJobs_ConfiguresDurableJobsJsonMetadata()
    {
        var builder = new TestSiloBuilder();

        builder.UseAzureBlobDurableJobs(options =>
        {
            options.ConfigureBlobServiceClient("UseDevelopmentStorage=true");
            options.ContainerName = "durable-jobs-test";
        });

        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(JsonJournalOptions));
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<JsonJournalOptions>>().Value;

        var durableJobsJsonContextType = typeof(DurableJob).Assembly.GetType("Orleans.DurableJobs.DurableJobsJsonContext", throwOnError: true)!;
        Assert.Contains(options.SerializerOptions.TypeInfoResolverChain, resolver => durableJobsJsonContextType.IsInstanceOfType(resolver));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UseAzureTableDurableJobs_ConfiguresJournalProviderAndDurableJobsJsonMetadata(bool useServiceCollection)
    {
        var builder = new TestSiloBuilder();
        static void Configure(AzureTableJournalStorageOptions options)
        {
            options.ConfigureTableServiceClient("UseDevelopmentStorage=true");
            options.TableName = "durablejobstest";
        }

        if (useServiceCollection)
        {
            Assert.Same(builder.Services, builder.Services.UseAzureTableDurableJobs(Configure));
        }
        else
        {
            Assert.Same(builder, builder.UseAzureTableDurableJobs(Configure));
        }

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IJournalStorageProvider));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IJournalStorageCatalog));
        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.Equal("durablejobstest", serviceProvider.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>().Value.TableName);
        var options = serviceProvider.GetRequiredService<IOptions<JsonJournalOptions>>().Value;
        var contextType = typeof(DurableJob).Assembly.GetType("Orleans.DurableJobs.DurableJobsJsonContext", throwOnError: true)!;
        Assert.Contains(options.SerializerOptions.TypeInfoResolverChain, resolver => contextType.IsInstanceOfType(resolver));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

#pragma warning restore ORLEANSEXP005
