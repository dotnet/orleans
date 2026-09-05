using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.LeaseProviders;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.PersistentStreams;
using Xunit;

namespace Tester.AzureUtils.Streaming;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("Streaming")]
public sealed class AzureStreamingArgumentValidationTests
{
    [Fact]
    public void UseAzureBlobLeaseProvider_NullConfigurator_Throws()
    {
        ISiloPersistentStreamConfigurator configurator = null!;

        var exception = Assert.Throws<ArgumentNullException>(
            () => configurator.UseAzureBlobLeaseProvider(_ => { }));

        Assert.Equal("configurator", exception.ParamName);
    }

    [Fact]
    public void AzureQueueDataAdapterV1_NullSerializer_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AzureQueueDataAdapterV1(null!));

        Assert.Equal("serializer", exception.ParamName);
    }

    [Fact]
    public void AzureQueueDataAdapterV2_NullSerializer_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AzureQueueDataAdapterV2(null!));

        Assert.Equal("serializer", exception.ParamName);
    }

    [Fact]
    public void AzureQueueDataManager_NullQueueName_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AzureQueueDataManager(NullLoggerFactory.Instance, null!, new AzureQueueOptions()));

        Assert.Equal("queueName", exception.ParamName);
    }

    [Fact]
    public void AzureQueueDataManager_NullOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AzureQueueDataManager(NullLoggerFactory.Instance, "queue-name", (AzureQueueOptions)null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task DeleteQueueMessage_NullMessage_Throws()
    {
        var manager = new AzureQueueDataManager(NullLoggerFactory.Instance, "queue-name", new AzureQueueOptions());

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => manager.DeleteQueueMessage((QueueMessage)null!));

        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public void SetSequenceToken_NullSerializer_ThrowsBeforeMutatingEntity()
    {
        var entity = new StreamDeliveryFailureEntity { SequenceToken = [1, 2, 3] };

        var exception = Assert.Throws<ArgumentNullException>(
            () => entity.SetSequenceToken(null!, token: null));

        Assert.Equal("serializer", exception.ParamName);
        Assert.Equal([1, 2, 3], entity.SequenceToken);
    }

    [Fact]
    public void GetSequenceToken_NullSerializer_Throws()
    {
        var entity = new StreamDeliveryFailureEntity { SequenceToken = [1, 2, 3] };

        var exception = Assert.Throws<ArgumentNullException>(
            () => entity.GetSequenceToken(null!));

        Assert.Equal("serializer", exception.ParamName);
    }

    [Theory]
    [InlineData(nameof(AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues))]
    [InlineData(nameof(AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues))]
    public async Task QueueUtility_NullQueueNamesWithOptions_Throws(string operation)
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeQueueUtility(operation));

        Assert.Equal("azureQueueNames", exception.ParamName);
    }

    [Theory]
    [InlineData(nameof(AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues))]
    [InlineData(nameof(AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues))]
    public async Task QueueUtility_NullQueueNamesWithConnectionString_Throws(string operation)
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeQueueUtilityWithConnectionString(operation));

        Assert.Equal("azureQueueNames", exception.ParamName);
    }

    [Theory]
    [InlineData(nameof(AzureBlobLeaseProvider.Acquire))]
    [InlineData(nameof(AzureBlobLeaseProvider.Release))]
    [InlineData(nameof(AzureBlobLeaseProvider.Renew))]
    public async Task LeaseOperation_NullLeases_ThrowsBeforeAccessingStorage(string operation)
    {
        var provider = new AzureBlobLeaseProvider(Options.Create(new AzureBlobLeaseProviderOptions()));

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeLeaseOperation(provider, operation));

        Assert.Equal(operation == nameof(AzureBlobLeaseProvider.Acquire) ? "leaseRequests" : "acquiredLeases", exception.ParamName);
    }

    [Fact]
    public void AzureTableStorageStreamFailureHandler_NullStorageOptions_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new AzureTableStorageStreamFailureHandler<StreamDeliveryFailureEntity>(
                null!,
                NullLoggerFactory.Instance,
                faultOnFailure: false,
                clusterId: "cluster",
                azureStorageOptions: null!));

        Assert.Equal("azureStorageOptions", exception.ParamName);
    }

    private static Task InvokeQueueUtility(string operation) =>
        operation switch
        {
            nameof(AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues) =>
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(null!, null!, (AzureQueueOptions)null!),
            nameof(AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues) =>
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(null!, null!, (AzureQueueOptions)null!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task InvokeQueueUtilityWithConnectionString(string operation) =>
        operation switch
        {
            nameof(AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues) =>
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(null!, null!, (string)null!),
            nameof(AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues) =>
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(null!, null!, (string)null!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task InvokeLeaseOperation(AzureBlobLeaseProvider provider, string operation) =>
        operation switch
        {
            nameof(AzureBlobLeaseProvider.Acquire) => provider.Acquire("category", null!),
            nameof(AzureBlobLeaseProvider.Release) => provider.Release("category", null!),
            nameof(AzureBlobLeaseProvider.Renew) => provider.Renew("category", null!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
