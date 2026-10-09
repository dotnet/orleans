using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableMessaging.Tests.Functional;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class StandardJournaledStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dictionary_WriteAcknowledgementPreservesLaterMutations(bool snapshot)
    {
        var id = new JournalId("standard-dictionary/" + Guid.NewGuid().ToString("N"));
        var storage = new ControlledJournalStorageProvider();
        await using var provider = CreateProvider(storage, id);
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var items = scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state");
        Assert.False(owner is IDurableStateManager);
        Assert.True(owner.TryGetStateMachine("state", out var registered));
        Assert.Same(items, registered);
        await owner.InitializeAsync(TestContext.Current.CancellationToken);
        items.Add("first", 1);
        if (snapshot) storage.RequestSnapshot(id);
        using var blocked = storage.BlockWrite(id);
        var first = owner.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await blocked.WaitUntilEnteredAsync();
        items["later"] = 2;
        Assert.False(first.IsCompleted);
        blocked.Release();
        await first;
        await using (var replay = provider.CreateAsyncScope())
        {
            var recovered = replay.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            var persisted = replay.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state");
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new KeyValuePair<string, int>("first", 1), Assert.Single(persisted));
        }
        Assert.Equal(2, items["later"]);
        await owner.WriteStateAsync(TestContext.Current.CancellationToken);
        await using var finalScope = provider.CreateAsyncScope();
        var finalOwner = finalScope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var final = finalScope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state");
        await finalOwner.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { new KeyValuePair<string, int>("first", 1), new KeyValuePair<string, int>("later", 2) }, final.OrderBy(pair => pair.Key));
        Assert.Equal(2, storage.GetSuccessfulWriteCount(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Value_WriteAcknowledgementPreservesNewerPendingValue(bool snapshot)
    {
        var id = new JournalId("standard-value/" + Guid.NewGuid().ToString("N"));
        var storage = new ControlledJournalStorageProvider();
        await using var provider = CreateProvider(storage, id);
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var value = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("state");
        await owner.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 1;
        if (snapshot) storage.RequestSnapshot(id);
        using var blocked = storage.BlockWrite(id);
        var first = owner.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await blocked.WaitUntilEnteredAsync();
        value.Value = 2;
        blocked.Release();
        await first;
        await using (var replay = provider.CreateAsyncScope())
        {
            var recovered = replay.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            var persisted = replay.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("state");
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, persisted.Value);
        }
        Assert.Equal(2, value.Value);
        await owner.WriteStateAsync(TestContext.Current.CancellationToken);
        await using var finalScope = provider.CreateAsyncScope();
        var finalOwner = finalScope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var final = finalScope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("state");
        await finalOwner.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, final.Value);
        Assert.Equal(2, storage.GetSuccessfulWriteCount(id));
    }

    private static ServiceProvider CreateProvider(ControlledJournalStorageProvider storage, JournalId id)
    {
        var builder = InboxStateManagerBoundaryTests.CreateBuilder("orleans-binary");
        builder.Services.AddSingleton<IJournalStorageProvider>(sp =>
        {
            storage.Configure(sp.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
            return storage;
        });
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(id));
        return builder.Services.BuildServiceProvider(validateScopes: true);
    }
}
