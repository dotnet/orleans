using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Concurrency;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Functional;
using Orleans.Messaging.Tests.Support;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class ActivationValidationTests
{
    [Fact]
    public void StatelessWorker_FailsWithSpecificDiagnostic()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MessagingActivationValidator.Validate(typeof(StatelessGrain), new StatelessWorkerAttribute().PlacementStrategy));

        Assert.Contains("one activation", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(StatelessGrain).ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ReentrantGrain))]
    [InlineData(typeof(MayInterleaveGrain))]
    [InlineData(typeof(InterleavableGrain))]
    public void ReentrantAndInterleavingModels_AreAccepted(Type grainClass) =>
        MessagingActivationValidator.Validate(grainClass, new RandomPlacementAttribute().PlacementStrategy);

    [Fact]
    public async Task StandardStateConstruction_MissingSelectedCodecFailsBeforeEnrollment()
    {
        const string selectedFormat = "orleans-binary";
        var builder = InboxStateManagerBoundaryTests.CreateBuilder(selectedFormat);
        builder.Services.RemoveAllKeyed(typeof(IDurableValueCommandCodec<>), selectedFormat);
        var id = new JournalId("missing-codec/" + Guid.NewGuid().ToString("N"));
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(id));
        await using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("state"));
        Assert.Contains(selectedFormat, exception.Message, StringComparison.Ordinal);
        Assert.False(owner.TryGetStateMachine("state", out _));
    }

    [Fact]
    public async Task CodecResolution_PreservesUnrelatedFailure()
    {
        var cause = new InvalidOperationException("codec resolution failure");
        const string selectedFormat = "orleans-binary";
        var builder = InboxStateManagerBoundaryTests.CreateBuilder(selectedFormat);
        builder.Services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, int>>(selectedFormat, (_, _) => throw cause);
        var id = new JournalId("codec-resolution/" + Guid.NewGuid().ToString("N"));
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(id));
        await using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"));

        Assert.Same(cause, exception);
        Assert.False(owner.TryGetStateMachine("state", out _));
    }

    [Reentrant]
    private sealed class ReentrantGrain;

    [StatelessWorker]
    private sealed class StatelessGrain;

    [MayInterleave(nameof(Interleave))]
    private sealed class MayInterleaveGrain
    {
        public static bool Interleave(IInvokable request) => true;
    }

    public interface IInterleavableBase
    {
        [AlwaysInterleave]
        Task PingAsync();
    }

    public interface IInterleavableGrain : IGrain, IInterleavableBase;

    private sealed class InterleavableGrain : IInterleavableGrain
    {
        public Task PingAsync() => Task.CompletedTask;
    }
}
