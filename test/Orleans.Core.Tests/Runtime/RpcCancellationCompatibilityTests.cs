using System.Reflection;
using Orleans.Core.Internal;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestCategory("BVT")]
public class RpcCancellationCompatibilityTests
{
    [Fact]
    public async Task IMemoryStorageGrain_CancellationOverloads_DelegateToLegacyImplementation()
    {
        IMemoryStorageGrain grain = new LegacyMemoryStorageGrain();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var state = new GrainState<int>(42);

        var readState = await grain.ReadStateAsync<int>("key", cancellation.Token);
        Assert.Equal(42, readState?.State);
        Assert.Equal("etag", await grain.WriteStateAsync("key", state, cancellation.Token));
        await grain.DeleteStateAsync<int>("key", "etag", cancellation.Token);

        var implementation = Assert.IsType<LegacyMemoryStorageGrain>(grain);
        Assert.Equal(["read", "write", "delete"], implementation.Operations);
    }

    [Fact]
    public async Task IAsyncEnumerableGrainExtension_CancellationDisposeOverload_DelegatesToLegacyImplementation()
    {
        IAsyncEnumerableGrainExtension extension = new LegacyAsyncEnumerableExtension();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var requestId = Guid.NewGuid();

        await extension.DisposeAsync(requestId, cancellation.Token);

        Assert.Equal(requestId, Assert.IsType<LegacyAsyncEnumerableExtension>(extension).DisposedRequestId);
    }

    [Fact]
    public async Task IGrainManagementExtension_CancellationOverloads_DelegateToLegacyImplementation()
    {
        IGrainManagementExtension extension = new LegacyGrainManagementExtension();

        await extension.DeactivateOnIdle(TestContext.Current.CancellationToken);
        await extension.MigrateOnIdle(TestContext.Current.CancellationToken);

        Assert.Equal(["deactivate", "migrate"], ((LegacyGrainManagementExtension)extension).Operations);
    }

    [Theory]
    [InlineData(typeof(IManagementGrain), nameof(IManagementGrain.GetHosts), "GetHosts", "4C0864C2")]
    [InlineData(typeof(IMemoryStorageGrain), nameof(IMemoryStorageGrain.ReadStateAsync), "ReadStateAsync", "45659318")]
    [InlineData(typeof(IAsyncEnumerableGrainExtension), nameof(IAsyncEnumerableGrainExtension.DisposeAsync), "DisposeAsync", "3C6D7209")]
    [InlineData(typeof(IGrainManagementExtension), nameof(IGrainManagementExtension.DeactivateOnIdle), "DeactivateOnIdle", "1B9614D1")]
    [InlineData(typeof(IGrainManagementExtension), nameof(IGrainManagementExtension.MigrateOnIdle), "MigrateOnIdle", "4CC93B45")]
    public void PublicCancellationOverload_UsesLegacyWireAlias(
        Type interfaceType,
        string methodName,
        string expectedLegacyAlias,
        string expectedCancellationAlias)
    {
        var methods = interfaceType.GetMethods().Where(method => method.Name == methodName).ToArray();
        var legacyMethod = Assert.Single(methods, method =>
            method.GetParameters() is not [.., { ParameterType: var parameterType }]
            || parameterType != typeof(CancellationToken));
        var cancellationMethod = Assert.Single(methods, method =>
            method.GetParameters() is [.., { ParameterType: var parameterType }]
            && parameterType == typeof(CancellationToken));

        Assert.Equal(expectedLegacyAlias, legacyMethod.GetCustomAttribute<AliasAttribute>()?.Alias);
        Assert.Equal(expectedCancellationAlias, cancellationMethod.GetCustomAttribute<AliasAttribute>()?.Alias);
    }

    private sealed class LegacyMemoryStorageGrain : IMemoryStorageGrain
    {
        public List<string> Operations { get; } = [];

        public Task<IGrainState<T>?> ReadStateAsync<T>(string grainStoreKey)
        {
            Operations.Add("read");
            return Task.FromResult<IGrainState<T>?>(new GrainState<T>((T)(object)42));
        }

        public Task<string> WriteStateAsync<T>(string grainStoreKey, IGrainState<T> grainState)
        {
            Operations.Add("write");
            return Task.FromResult("etag");
        }

        public Task DeleteStateAsync<T>(string grainStoreKey, string? eTag)
        {
            Operations.Add("delete");
            return Task.CompletedTask;
        }
    }

    private sealed class LegacyAsyncEnumerableExtension : IAsyncEnumerableGrainExtension
    {
        public Guid DisposedRequestId { get; private set; }

        public ValueTask<(EnumerationResult Status, object? Value)> StartEnumeration<T>(
            Guid requestId,
            IAsyncEnumerableRequest<T> request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<(EnumerationResult Status, object? Value)> MoveNext<T>(
            Guid requestId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync(Guid requestId)
        {
            DisposedRequestId = requestId;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LegacyGrainManagementExtension : IGrainManagementExtension
    {
        public List<string> Operations { get; } = [];

        public ValueTask DeactivateOnIdle()
        {
            Operations.Add("deactivate");
            return ValueTask.CompletedTask;
        }

        public ValueTask MigrateOnIdle()
        {
            Operations.Add("migrate");
            return ValueTask.CompletedTask;
        }
    }
}
