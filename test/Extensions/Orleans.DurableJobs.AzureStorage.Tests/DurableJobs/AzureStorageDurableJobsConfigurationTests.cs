using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling.Json;
using Orleans.Hosting;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

#pragma warning disable ORLEANSEXP005

public class AzureStorageDurableJobsConfigurationTests
{
    [Fact]
    public void UseAzureBlobDurableJobs_ComposesDurableJobsAndApplicationJsonMetadata()
    {
        var builder = new TestSiloBuilder();

        builder.UseAzureBlobDurableJobs(
            options =>
            {
                options.ConfigureBlobServiceClient("UseDevelopmentStorage=true");
                options.ContainerName = "durable-jobs-test";
            },
            AzureStorageDurableJobsConfigurationTestJsonContext.Default);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<JsonJournalOptions>();

        Assert.NotNull(options.SerializerOptions.GetTypeInfo(typeof(DurableJob)));
        Assert.NotNull(options.SerializerOptions.GetTypeInfo(typeof(TestPayload)));
    }

    public sealed record TestPayload(string Value);

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

#pragma warning restore ORLEANSEXP005

[JsonSerializable(typeof(AzureStorageDurableJobsConfigurationTests.TestPayload))]
internal sealed partial class AzureStorageDurableJobsConfigurationTestJsonContext : JsonSerializerContext;
