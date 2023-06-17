using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

/// <summary>
/// Tests support for grain methods which return <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
public class AsyncEnumerableGrainCallTests : HostedTestClusterEnsureDefaultStarted
{
    public AsyncEnumerableGrainCallTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(200);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        await foreach (var entry in grain.GetValues())
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(5, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Batch()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues())
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SplitBatch()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues().WithBatchSize(25))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_NoBatching()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues().WithBatchSize(1))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));

        // One call for every value and one final call to complete the enumeration
        Assert.Equal(values.Count + 1, moveNextCallCount);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_WithCancellation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(200);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        var cts = new CancellationTokenSource();
        await foreach (var entry in grain.GetValues().WithCancellation(cts.Token))
        {
            values.Add(entry);
            if (values.Count == 3)
            {
                cts.Cancel();
            }

            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowProducer()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(2000);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        await foreach (var entry in grain.GetValues())
        {
            values.Add(entry);
            if (values.Count == 2)
            {
                break;
            }

            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(2, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Deactivate()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 2))
            {
                await Task.Delay(200);
                await grain.OnNext(value.ToString());
            }

            await grain.Deactivate();
        });

        var values = new List<string>();
        await Assert.ThrowsAsync<EnumerationAbortedException>(async () =>
        {
            await foreach (var entry in grain.GetValues())
            {
                values.Add(entry);
                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        });

        Assert.Equal(2, values.Count);
    }
}
