using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class DurableStateAndTcsRecoveryTests : JournalingTestBase
{
    [Fact]
    public async Task OrleansBinaryCodec_StateAndTcs_WriteAndRecover()
    {
        var sut = CreateTestSystem();
        var state = new DurableState<string>("state", sut.Manager, new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>()));
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>()),
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();

        ((IStorage<string>)state).State = "state-value";
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystem(storage: sut.Storage);
        var state2 = new DurableState<string>("state", sut2.Manager, new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>()));
        var tcs2 = new DurableTaskCompletionSource<int>(
            "tcs",
            sut2.Manager,
            new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>()),
            Copier<int>(),
            Copier<Exception>());
        await sut2.Lifecycle.OnStart();

        Assert.Equal("state-value", ((IStorage<string>)state2).State);
        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, tcs2.State.Status);
        Assert.Equal(17, tcs2.State.Value);
        Assert.Equal(17, await tcs2.Task);
    }

    private ILogValueCodec<T> ValueCodec<T>() => new OrleansLogValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();
}
