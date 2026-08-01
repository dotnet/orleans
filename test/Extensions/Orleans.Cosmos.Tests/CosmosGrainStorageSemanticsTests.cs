using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace Tester.Cosmos.Persistence;

public class CosmosGrainStorageSemanticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ClearStateAsync_DeleteWithoutETagDoesNotAccessStorage(string? etag)
    {
        var executor = new ThrowingOperationExecutor();
        var storage = CreateStorage(new CosmosGrainStorageOptions
        {
            DeleteStateOnClear = true,
            OperationExecutor = executor
        });
        var state = new TestState();
        var grainState = new GrainState<TestState>(state)
        {
            ETag = etag,
            RecordExists = true
        };

        await storage.ClearStateAsync("grain-type", GrainId.Create("type", "key"), grainState);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal(etag, grainState.ETag);
        Assert.Same(state, grainState.State);
        Assert.False(grainState.RecordExists);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, null)]
    [InlineData(HttpStatusCode.PreconditionFailed, "*")]
    [InlineData(HttpStatusCode.NotFound, "*")]
    public async Task WriteStateAsync_MapsOptimisticConcurrencyFailures(HttpStatusCode statusCode, string? etag)
    {
        var storage = CreateStorage(new CosmosGrainStorageOptions
        {
            OperationExecutor = new ThrowingOperationExecutor(statusCode)
        });
        var grainState = new GrainState<TestState>(new TestState())
        {
            ETag = etag
        };

        await Assert.ThrowsAsync<CosmosConditionNotSatisfiedException>(
            () => storage.WriteStateAsync("grain-type", GrainId.Create("type", "key"), grainState));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, false, null)]
    [InlineData(HttpStatusCode.PreconditionFailed, false, "*")]
    [InlineData(HttpStatusCode.NotFound, true, "*")]
    public async Task ClearStateAsync_MapsOptimisticConcurrencyFailures(
        HttpStatusCode statusCode,
        bool deleteStateOnClear,
        string? etag)
    {
        var storage = CreateStorage(new CosmosGrainStorageOptions
        {
            DeleteStateOnClear = deleteStateOnClear,
            OperationExecutor = new ThrowingOperationExecutor(statusCode)
        });
        var grainState = new GrainState<TestState>(new TestState())
        {
            ETag = etag
        };

        await Assert.ThrowsAsync<CosmosConditionNotSatisfiedException>(
            () => storage.ClearStateAsync("grain-type", GrainId.Create("type", "key"), grainState));
    }

    private static CosmosGrainStorage CreateStorage(CosmosGrainStorageOptions options)
    {
        var clusterOptions = Options.Create(new ClusterOptions { ServiceId = "service" });
        return new CosmosGrainStorage(
            "test",
            options,
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider(),
            clusterOptions,
            new DefaultDocumentIdProvider(clusterOptions),
            new ThrowingActivatorProvider());
    }

    private sealed class ThrowingOperationExecutor : ICosmosOperationExecutor
    {
        private readonly HttpStatusCode? _statusCode;

        public ThrowingOperationExecutor(HttpStatusCode? statusCode = null)
        {
            _statusCode = statusCode;
        }

        public int CallCount { get; private set; }

        public Task<TResult> ExecuteOperation<TArg, TResult>(Func<TArg, Task<TResult>> func, TArg arg)
        {
            CallCount++;
            if (_statusCode is not { } statusCode)
            {
                throw new InvalidOperationException("Storage should not have been accessed.");
            }

            return Task.FromException<TResult>(new CosmosException("Test failure", statusCode, 0, string.Empty, 0));
        }
    }

    private sealed class ThrowingActivatorProvider : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => throw new InvalidOperationException("State should not be activated.");
    }

    private sealed class TestState
    {
    }
}
