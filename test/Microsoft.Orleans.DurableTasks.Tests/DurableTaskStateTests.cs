#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public class DurableTaskStateTests
{
    [Fact]
    public void NewInstance_HasEmptyCollectionsAndNullTimestamps()
    {
        var state = new DurableTaskState();

        Assert.Empty(state.CompletionDestinations);
        Assert.Null(state.Result);
        Assert.Null(state.Request);
        Assert.Null(state.RequestPayload);
        Assert.Null(state.ResultPayload);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.CancellationRequestedAt);
        Assert.Equal(default, state.CreatedAt);
    }

    [Fact]
    public void SetResult_StoresOrleansPayloadAndCachesValue()
    {
        var serializer = CreateSerializer();
        var state = new DurableTaskState().Bind(serializer);
        var response = DurableTaskResponse.FromResult("value");

        state.SetResult(response);

        Assert.Same(response, state.Result);
        var decoded = Assert.IsType<DurableTaskResponse<string>>(
            serializer.Deserialize<DurableTaskResponse>(state.ResultPayload!));
        Assert.Equal("value", decoded.GetResult<string>());

        state.SetResult(null);

        Assert.Null(state.Result);
        Assert.Null(state.ResultPayload);
    }

    [Fact]
    public void PayloadDecodesOnFirstAccessAndCachesDecodedValue()
    {
        var serializer = CreateSerializer();
        var state = new DurableTaskState
        {
            ResultPayload = serializer.SerializeToArray<DurableTaskResponse>(DurableTaskResponse.FromResult(7)),
        }.Bind(serializer);

        var first = state.Result;

        Assert.Equal(7, Assert.IsType<DurableTaskResponse<int>>(first).GetResult<int>());
        Assert.Same(first, state.Result);
    }

    [Fact]
    public void UnboundStateRejectsPayloadAccess()
    {
        var serializer = CreateSerializer();
        var state = new DurableTaskState
        {
            ResultPayload = serializer.SerializeToArray<DurableTaskResponse>(DurableTaskResponse.Completed),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => state.Result);

        Assert.Equal("The durable task state is not bound to a durable task storage.", exception.Message);
        Assert.Throws<InvalidOperationException>(() => state.SetResult(DurableTaskResponse.Completed));
    }

    private static Serializer CreateSerializer() =>
        new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskState).Assembly))
            .BuildServiceProvider()
            .GetRequiredService<Serializer>();
}