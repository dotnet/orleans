using System.Collections;
using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Timers;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class MessagingProviderCutoverTests
{
    private const string OwnershipKey = "orleans.messaging.ownership-id";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicHosting_NamedFactories_CommitsCompositeEndpointsAndRecoversFreshActivation()
    {
        var state = new ControlledJournalStorageProvider();
        var snapshots = new NamedFactoryMessagingProbe();
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddVolatileJournalStorage();
            silo.AddJournalStorage("state", services =>
            {
                state.Configure(services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
                return state;
            });
            silo.AddVolatileJournalStorage("jobs");
            silo.UseJournaledDurableJobs(options => options.ActiveProviderName = "jobs");
            silo.AddDurableMessaging();
            silo.Services.AddSingleton(snapshots);
            silo.Services.AddKeyedScoped<IDurableDictionary<Guid, DurableEffect>>("cutover-effects", (services, _) =>
            {
                var owner = services.GetRequiredService<IJournaledStateManager>();
                var effects = new ObservedJournalDictionary<Guid, DurableEffect>(owner);
                owner.RegisterStateMachine("cutover-effects", effects);
                return effects;
            });
            silo.Services.AddScoped<IJournaledStateManager>(services =>
            {
                var context = services.GetRequiredService<IGrainContext>();
                var manager = services.GetRequiredKeyedService<IJournaledStateManagerFactory>("state")
                    .CreateStandalone(JournalId.FromGrainId(context.GrainId));
                ((ILifecycleParticipant<IGrainLifecycle>)manager).Participate(context.ObservableLifecycle);
                return manager;
            });
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync(Token);
        var sender = cluster.Client.GetGrain<INamedFactoryMessagingGrain>(Guid.NewGuid());
        var receiver = cluster.Client.GetGrain<INamedFactoryMessagingGrain>(Guid.NewGuid());
        var sink = cluster.Client.GetGrain<INamedFactoryMessagingGrain>(Guid.NewGuid());
        var logicalId = Guid.NewGuid();
        var receivedTask = snapshots.WaitAsync(receiver.GetGrainId(), Token);
        var forwardedTask = snapshots.WaitAsync(sink.GetGrainId(), Token);
        await sender.SendAsync(receiver.GetGrainId(), "messages/record", new DurableTestMessage(logicalId, 1, "composite", sink.GetGrainId()));
        var received = await receivedTask;
        var forwarded = await forwardedTask;
        Assert.Equal(0, received.OutboxCount);
        Assert.Equal(0, forwarded.OutboxCount);
        Assert.Equal(new DurableEffect(logicalId, 1, 1, "composite"), Assert.Single(received.Effects));
        Assert.Equal(Assert.Single(received.Effects), Assert.Single(forwarded.Effects));
        Assert.True(cluster.TryGetGrainContext(receiver.GetGrainId(), out var oldContext));
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Assert.False(oldManager is IDurableStateManager);
        await receiver.RequestDeactivationAsync();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(received.ActivationId, recovered.ActivationId);
        Assert.Equal(received.Effects, recovered.Effects);
        Assert.True(cluster.TryGetGrainContext(receiver.GetGrainId(), out var newContext));
        Assert.NotSame(oldManager, newContext.ActivationServices.GetRequiredService<IJournaledStateManager>());
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        Assert.NotNull(await state.CreateStorage(journal).GetMetadataAsync(Token));
        Assert.Null(await cluster.Silos[0].ServiceProvider.GetRequiredService<IJournalStorageProvider>().CreateStorage(journal).GetMetadataAsync(Token));
    }

    [Fact]
    public async Task NamedFactories_OutsideGrain_RecoverWithMessagingParticipantsRegistered()
    {
        await using var fixture = new CutoverFixture();
        await fixture.InitializeAsync();
        var services = fixture.Services;
        var id = new JournalId("integration/non-grain-state");
        foreach (var (provider, expected) in new[] { ("A", 11), ("B", 22) })
        {
            var factory = services.GetRequiredKeyedService<IJournaledStateManagerFactory>(provider);
            await using var manager = factory.CreateStandalone(id);
            var value = new ObservedJournalValue<int>(manager);
            manager.RegisterStateMachine("value", value);
            await manager.InitializeAsync(Token);
            Assert.Equal(0, value.Value);
            value.Value = expected;
            await manager.WriteStateAsync(Token);
            value.Value = expected + 100;
            (provider == "A" ? fixture.A : fixture.B).FailWrite(id);
            var failure = await Assert.ThrowsAsync<IOException>(() => manager.WriteStateAsync(Token).AsTask());
            var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(Token).AsTask());
            Assert.Same(failure, fenced.InnerException);
        }

        foreach (var (provider, expected) in new[] { ("A", 11), ("B", 22) })
        {
            var factory = services.GetRequiredKeyedService<IJournaledStateManagerFactory>(provider);
            await using var manager = factory.CreateStandalone(id);
            var value = new ObservedJournalValue<int>(manager);
            manager.RegisterStateMachine("value", value);
            await manager.InitializeAsync(Token);
            Assert.Equal(expected, value.Value);
        }

        Assert.Null(await services.GetRequiredService<IJournalStorageProvider>().CreateStorage(id).GetMetadataAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedFactories_ExplicitJournalId_ReplaysMessagingParticipantsInSelectedNamespace(bool outbox)
    {
        await using var fixture = new CutoverFixture();
        await fixture.InitializeAsync();
        var id = new JournalId("integration/explicit-endpoint");
        var a = await fixture.OpenAsync("A", id);
        Assert.NotEqual(id, JournalId.FromGrainId(a.GrainId));
        var first = fixture.Envelope(a, outbox);
        await a.CommitAsync(first, outbox);
        var handleA = a.AssertOwner(outbox);
        var b = await fixture.OpenAsync("B", id);
        Assert.Equal(0, b.Count(outbox));
        Assert.Null(b.Handle(outbox).Value);
        Assert.Null(b.Generation(outbox).Value);
        var second = fixture.Envelope(b, outbox);
        await b.CommitAsync(second, outbox);
        var handleB = b.AssertOwner(outbox);
        Assert.NotEqual(handleA.Id, handleB.Id);
        await a.DisposeAsync();
        await b.DisposeAsync();

        a = await fixture.OpenAsync("A", id);
        b = await fixture.OpenAsync("B", id);

        Assert.Equal(1, a.Count(outbox));
        Assert.Equal(1, b.Count(outbox));
        AssertHandle(handleA, a.AssertOwner(outbox));
        AssertHandle(handleB, b.AssertOwner(outbox));
        Assert.Equal(first.MessageId, Assert.Single(a.Messages(outbox)).MessageId);
        Assert.Equal(second.MessageId, Assert.Single(b.Messages(outbox)).MessageId);
        Assert.NotNull(await fixture.A.CreateStorage(id).GetMetadataAsync(Token));
        Assert.NotNull(await fixture.B.CreateStorage(id).GetMetadataAsync(Token));
        Assert.Null(await fixture.Services.GetRequiredService<IJournalStorageProvider>().CreateStorage(id).GetMetadataAsync(Token));
        foreach (var name in new[] { "__orleans.durable-messaging.outbox", "__orleans.durable-messaging.inbox" })
        {
            Assert.True(a.Manager.TryGetStateMachine(name, out var messagingState));
            Assert.Equal(typeof(IDurableOutbox).Assembly, messagingState.GetType().Assembly);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCutover_AmbiguousSchedules_DrainsCommittedOwnerAndDeduplicatesEffects(bool outbox)
    {
        await using var fixture = new CutoverFixture();
        await fixture.InitializeAsync();
        var old = await fixture.OpenAsync("state", new JournalId("integration/old-owner"));
        var retried = await fixture.OpenAsync("state", new JournalId("integration/retried-owner"));
        var sink = await fixture.OpenAsync("state", new JournalId("integration/sink"));
        var first = fixture.Envelope(old, outbox, sink);
        var second = fixture.Envelope(retried, outbox, sink);
        fixture.Jobs.DuplicateNext = true;
        await old.CommitAsync(first, outbox);
        var committedA = old.AssertOwner(outbox);
        var duplicateA = fixture.Jobs.Scheduled.Last();
        Assert.NotEqual(committedA.Id, duplicateA.Id);
        Assert.Equal(committedA.Metadata![OwnershipKey], duplicateA.Metadata![OwnershipKey]);
        fixture.Jobs.FailAfterNext = true;
        await Assert.ThrowsAsync<IOException>(() => retried.CommitAsync(second, outbox));
        var ambiguousA = fixture.Jobs.Scheduled.Last();
        Assert.Equal(0, retried.Fault.FailureCount);
        Assert.False(retried.Fault.Failure.Task.IsCompleted);
        Assert.Equal(0, retried.Count(outbox));
        await retried.Manager.WriteStateAsync(Token);
        retried = await fixture.ReopenAsync(retried);
        Assert.Equal(0, retried.Count(outbox));
        Assert.Null(retried.Handle(outbox).Value);
        Assert.Null(retried.Generation(outbox).Value);

        await fixture.CutoverAsync();
        old = await fixture.ReopenAsync(old);
        Assert.Equal(3, fixture.Jobs.Scheduled.Count);
        AssertHandle(committedA, old.AssertOwner(outbox));
        var drain = Assert.Single(fixture.Jobs.Shards, shard => shard.Id == committedA.ShardId);
        Assert.True(drain.IsAddingCompleted);
        Assert.Equal(3, await drain.GetJobCountAsync());
        var drainMetadata = await fixture.A.CreateStorage(ShardJournal(drain)).GetMetadataAsync(Token);
        Assert.NotNull(drainMetadata);
        Assert.Equal(fixture.Silo.ToParsableString(), drainMetadata.Properties["DurableJobsOwner"]);
        Assert.Null(await fixture.B.CreateStorage(ShardJournal(drain)).GetMetadataAsync(Token));

        fixture.Jobs.FailAfterNext = true;
        await Assert.ThrowsAsync<IOException>(() => retried.CommitAsync(second, outbox));
        var ambiguousB = fixture.Jobs.Scheduled.Last();
        Assert.Equal(0, retried.Fault.FailureCount);
        Assert.False(retried.Fault.Failure.Task.IsCompleted);
        Assert.Equal(0, retried.Count(outbox));
        await retried.Manager.WriteStateAsync(Token);
        retried = await fixture.ReopenAsync(retried);
        Assert.Equal(0, retried.Count(outbox));
        Assert.Null(retried.Handle(outbox).Value);
        Assert.Null(retried.Generation(outbox).Value);
        await retried.CommitAsync(second, outbox);
        var committedB = retried.AssertOwner(outbox);
        Assert.NotEqual(committedA.ShardId, committedB.ShardId);
        Assert.NotEqual(ambiguousA.ShardId, ambiguousB.ShardId);
        Assert.Equal(3, new[] { ambiguousA.Id, ambiguousB.Id, committedB.Id }.Distinct().Count());
        Assert.Equal(1, old.Count(outbox));
        Assert.Equal(1, retried.Count(outbox));

        await fixture.AssertDrainOccupiedAsync(1);
        await fixture.AssertTerminalCallbackAsync(old, duplicateA, outbox);
        await fixture.AssertTerminalCallbackAsync(retried, ambiguousA, outbox);
        await fixture.AssertTerminalCallbackAsync(retried, ambiguousB, outbox);
        AssertHandle(committedA, old.AssertOwner(outbox));
        AssertHandle(committedB, retried.AssertOwner(outbox));
        var stale = await fixture.Jobs.ScheduleJobAsync(new ScheduleJobRequest
        {
            Target = old.GrainId,
            JobName = JobName(outbox),
            DueTime = fixture.Clock.GetUtcNow(),
            Metadata = new Dictionary<string, string> { [OwnershipKey] = "stale-generation" }
        }, Token);
        await fixture.AssertTerminalCallbackAsync(old, stale, outbox);
        AssertHandle(committedA, old.AssertOwner(outbox));

        await fixture.DrainAsync();
        var firstReceiver = outbox ? sink : old;
        var secondReceiver = outbox ? sink : retried;
        Assert.Equal(DeliveryStatus.Duplicate, (await firstReceiver.InboxExtension.DeliverAsync(first, Token)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await secondReceiver.InboxExtension.DeliverAsync(second, Token)).Status);
        Assert.Equal(1, firstReceiver.Effects[first.MessageId]);
        Assert.Equal(1, secondReceiver.Effects[second.MessageId]);
        Assert.Equal(outbox ? 2 : 1, firstReceiver.Effects.Count);
        Assert.Equal(outbox ? 2 : 1, secondReceiver.Effects.Count);
        Assert.Null(old.Handle(outbox).Value);
        Assert.Null(retried.Handle(outbox).Value);
        Assert.Null(old.Generation(outbox).Value);
        Assert.Null(retried.Generation(outbox).Value);
        Assert.Equal(fixture.Jobs.Scheduled.Count, fixture.TerminalJobs.Count);
        await fixture.AssertRetiredAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProviderCutover_OwnerCommitFailure_ReplaysCompletePairOrReclaimsOrphans(bool outbox, bool committedBeforeFailure)
    {
        await using var fixture = new CutoverFixture();
        await fixture.InitializeAsync();
        var id = new JournalId("integration/crash-owner");
        var owner = await fixture.OpenAsync("state", id);
        var sink = await fixture.OpenAsync("state", new JournalId("integration/crash-sink"));
        var message = fixture.Envelope(owner, outbox, sink);
        fixture.Jobs.DuplicateNext = true;
        if (committedBeforeFailure)
        {
            fixture.State.FailAfterWrite(id);
            await Assert.ThrowsAsync<IOException>(() => owner.CommitAsync(message, outbox));
        }
        else
        {
            var write = fixture.State.BlockWrite(id);
            var commit = owner.CommitAsync(message, outbox);
            await write.WaitUntilEnteredAsync();
            try
            {
                Assert.Equal(2, fixture.Jobs.Scheduled.Count);
                foreach (var scheduled in fixture.Jobs.Scheduled)
                {
                    var callback = Substitute.For<IJobRunContext>();
                    callback.Job.Returns(scheduled);
                    callback.RunId.Returns("before-owner-commit");
                    Assert.True((await owner.Handler(outbox).ExecuteJobAsync(callback, Token)).IsInProgress);
                }
            }
            finally
            {
                write.Fail();
            }

            await Assert.ThrowsAsync<IOException>(() => commit);
        }
        var scheduledA = fixture.Jobs.Scheduled.ToArray();
        Assert.Equal(2, scheduledA.Length);
        Assert.NotEqual(scheduledA[0].Id, scheduledA[1].Id);
        await owner.AssertFencedAsync();
        owner = await fixture.ReopenAsync(owner);
        Assert.Equal(committedBeforeFailure ? 1 : 0, owner.Count(outbox));
        if (committedBeforeFailure)
        {
            AssertHandle(scheduledA[0], owner.AssertOwner(outbox));
        }
        else
        {
            Assert.Null(owner.Generation(outbox).Value);
            Assert.Null(owner.Handle(outbox).Value);
            foreach (var orphan in scheduledA) await fixture.AssertTerminalCallbackAsync(owner, orphan, outbox);
            Assert.Empty(owner.Effects);
            Assert.Empty(sink.Effects);
        }

        await fixture.CutoverAsync();
        if (!committedBeforeFailure)
        {
            await owner.CommitAsync(message, outbox);
            var committedB = owner.AssertOwner(outbox);
            Assert.NotEqual(scheduledA[0].Id, committedB.Id);
            Assert.NotEqual(scheduledA[0].ShardId, committedB.ShardId);
        }
        else
        {
            AssertHandle(scheduledA[0], owner.AssertOwner(outbox));
        }

        await fixture.DrainAsync();
        var receiver = outbox ? sink : owner;
        Assert.Equal(new[] { new KeyValuePair<Guid, int>(message.MessageId, 1) }, receiver.Effects);
        Assert.Equal(DeliveryStatus.Duplicate, (await receiver.InboxExtension.DeliverAsync(message, Token)).Status);
        Assert.Null(owner.Generation(outbox).Value);
        Assert.Null(owner.Handle(outbox).Value);
        Assert.Equal(fixture.Jobs.Scheduled.Count, fixture.TerminalJobs.Count);
        await fixture.AssertRetiredAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCutover_DrainJournalWriteFailure_RestartReplaysJobAndCleansOriginalProvider(bool outbox)
    {
        await using var fixture = new CutoverFixture();
        await fixture.InitializeAsync();
        var owner = await fixture.OpenAsync("state", new JournalId("integration/drain-write-failure"));
        var sink = await fixture.OpenAsync("state", new JournalId("integration/drain-write-sink"));
        var message = fixture.Envelope(owner, outbox, sink);
        await owner.CommitAsync(message, outbox);
        var handle = owner.AssertOwner(outbox);
        await fixture.CutoverAsync();
        var drain = Assert.Single(fixture.Jobs.Shards, shard => shard.Id == handle.ShardId);
        var id = ShardJournal(drain);
        var writesA = fixture.A.GetSuccessfulWriteCount(id);
        var writeId = ShardJournal(fixture.Jobs.WriteShard);
        var writesB = fixture.B.GetSuccessfulWriteCount(writeId);
        fixture.A.FailWrite(id);

        await fixture.AssertDrainOccupiedAsync(1);
        await Assert.ThrowsAsync<IOException>(() => drain.RemoveJobAsync(handle.Id, Token));
        await fixture.AssertDrainOccupiedAsync(1);
        Assert.Equal(writesA, fixture.A.GetSuccessfulWriteCount(id));
        Assert.Equal(writesB, fixture.B.GetSuccessfulWriteCount(writeId));
        var failedDrain = drain;
        await fixture.RestartShardManagerAsync();

        drain = Assert.Single(fixture.Jobs.Shards, shard => shard.Id == handle.ShardId);
        Assert.NotSame(failedDrain, drain);
        Assert.True(drain.IsAddingCompleted);
        Assert.Equal(1, await drain.GetJobCountAsync());
        AssertHandle(handle, owner.AssertOwner(outbox));
        Assert.Null(await fixture.B.CreateStorage(id).GetMetadataAsync(Token));
        await fixture.DrainAsync();
        Assert.Equal(writesA + 1, fixture.A.GetSuccessfulWriteCount(id));
        var receiver = outbox ? sink : owner;
        Assert.Equal(new[] { new KeyValuePair<Guid, int>(message.MessageId, 1) }, receiver.Effects);
        Assert.Equal(DeliveryStatus.Duplicate, (await receiver.InboxExtension.DeliverAsync(message, Token)).Status);
        await fixture.AssertRetiredAsync();
    }

    private static string JobName(bool outbox) => outbox ? "orleans.messaging.outbox-flush" : "orleans.messaging.inbox-drain";
    private static JournalId ShardJournal(IJobShard shard) => JournalId.Create("jobs", "shards", shard.Id);

    private static void AssertHandle(DurableJob expected, DurableJob actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ShardId, actual.ShardId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.TargetGrainId, actual.TargetGrainId);
        Assert.Equal(expected.Metadata![OwnershipKey], actual.Metadata![OwnershipKey]);
    }

    private sealed class CutoverFixture : IAsyncDisposable
    {
        private readonly List<ServiceProvider> _providers = [];
        private readonly List<Endpoint> _openedEndpoints = [];
        private readonly Dictionary<GrainId, Endpoint> _endpoints = [];
        private readonly IGrainFactory _grainFactory = Substitute.For<IGrainFactory>();
        private readonly Queue<Task<DeliveryResult>> _deliveries = new();
        private JobShardManager _manager = null!;
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        public ControlledJournalStorageProvider A { get; } = new();
        public ControlledJournalStorageProvider B { get; } = new();
        public ControlledJournalStorageProvider State { get; } = new();
        public SiloAddress Silo { get; } = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5123), 1);
        public ManualJobs Jobs { get; } = new();
        public HashSet<(string Id, string Shard)> TerminalJobs { get; } = [];
        public ServiceProvider Services { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Services = BuildServices("A");
            _manager = Services.GetRequiredService<JobShardManager>();
            Jobs.WriteShard = await CreateShardAsync();
            Jobs.Shards.Add(Jobs.WriteShard);
            _grainFactory.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>())
                .Returns(call =>
                {
                    var target = call.Arg<GrainId>();
                    var transport = Substitute.For<IDurableInboxExtension>();
                    transport.DeliverAsync(Arg.Any<DurableEnvelope>(), Arg.Any<CancellationToken>()).Returns(delivery =>
                    {
                        var task = _endpoints[target].InboxExtension.DeliverAsync(delivery.Arg<DurableEnvelope>(), delivery.Arg<CancellationToken>()).AsTask();
                        _deliveries.Enqueue(task);
                        return new ValueTask<DeliveryResult>(task);
                    });
                    return transport;
                });
        }

        private ServiceProvider BuildServices(string write)
        {
            var builder = new TestSiloBuilder();
            var services = builder.Services;
            services.AddSerializer();
            services.AddLogging();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, (sp, _) => sp.GetRequiredService<TimeProvider>());
            var local = Substitute.For<ILocalSiloDetails>();
            local.SiloAddress.Returns(Silo);
            services.AddSingleton(local);
            var membership = Substitute.For<IClusterMembershipService>();
            membership.CurrentSnapshot.Returns(new ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, new MembershipVersion(1)));
            services.AddSingleton(membership);
            builder.AddVolatileJournalStorage();
            foreach (var (name, storage) in new[] { ("A", A), ("B", B), ("state", State) })
            {
                builder.AddJournalStorage(name, sp =>
                {
                    storage.Configure(sp.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
                    return storage;
                });
            }

            builder.UseJournaledDurableJobs(options =>
            {
                options.ActiveProviderName = write;
                if (write == "B") options.DrainingProviderNames.Add("A");
            });
            builder.AddDurableMessaging(options =>
            {
                options.DeduplicationWindow = TimeSpan.FromHours(1);
                options.MaxOutboxRetryAge = TimeSpan.FromMinutes(5);
                options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(1);
            });
            services.AddSingleton<ILocalDurableJobManager>(Jobs);
            services.AddSingleton(_grainFactory);
            services.AddScoped<EndpointBinding>();
            services.AddScoped<IGrainContext>(sp => sp.GetRequiredService<EndpointBinding>().Context);
            services.AddScoped<IJournaledStateManager>(sp =>
            {
                var binding = sp.GetRequiredService<EndpointBinding>();
                return sp.GetRequiredKeyedService<IJournaledStateManagerFactory>(binding.Provider).CreateStandalone(binding.Id);
            });
            services.AddScoped<ManualTimers>();
            services.AddScoped<ITimerRegistry>(sp => sp.GetRequiredService<ManualTimers>());
            var result = services.BuildServiceProvider();
            _providers.Add(result);
            return result;
        }

        private Task<IJobShard> CreateShardAsync() => _manager.CreateShardAsync(
            Clock.GetUtcNow().AddHours(-1), Clock.GetUtcNow().AddHours(1), new Dictionary<string, string>(), Token);

        public async Task CutoverAsync()
        {
            var oldId = Jobs.WriteShard.Id;
            await _manager.UnregisterShardAsync(Jobs.WriteShard, Token);
            Jobs.Shards.Clear();
            Services = BuildServices("B");
            _manager = Services.GetRequiredService<JobShardManager>();
            var drained = Assert.Single(await _manager.AssignJobShardsAsync(Clock.GetUtcNow(), int.MaxValue, Token));
            Assert.Equal(oldId, drained.Id);
            Assert.True(drained.IsAddingCompleted);
            Jobs.Shards.Add(drained);
            Jobs.WriteShard = await CreateShardAsync();
            Jobs.Shards.Add(Jobs.WriteShard);
            Assert.NotEqual(drained.Id, Jobs.WriteShard.Id);
            Assert.Null(await A.CreateStorage(ShardJournal(Jobs.WriteShard)).GetMetadataAsync(Token));
        }

        public async Task RestartShardManagerAsync()
        {
            var writeId = Jobs.WriteShard.Id;
            foreach (var shard in Jobs.Shards) await shard.DisposeAsync();
            Jobs.Shards.Clear();
            Services = BuildServices("B");
            _manager = Services.GetRequiredService<JobShardManager>();
            Jobs.Shards.AddRange(await _manager.AssignJobShardsAsync(Clock.GetUtcNow(), int.MaxValue, Token));
            Jobs.WriteShard = Assert.Single(Jobs.Shards, shard => shard.Id == writeId);
        }

        public async Task<Endpoint> OpenAsync(string provider, JournalId id)
        {
            var scope = Services.CreateAsyncScope();
            var binding = scope.ServiceProvider.GetRequiredService<EndpointBinding>();
            binding.Provider = provider;
            binding.Id = id;
            binding.Context.GrainId.Returns(GrainId.Create("integration-endpoint", id.Value));
            var endpoint = new Endpoint(scope, binding.Context.GrainId, provider, id);
            _endpoints[endpoint.GrainId] = endpoint;
            _openedEndpoints.Add(endpoint);
            await endpoint.Manager.InitializeAsync(Token);
            return endpoint;
        }

        public async Task<Endpoint> ReopenAsync(Endpoint previous)
        {
            var manager = previous.Manager;
            var effects = previous.Effects;
            var inbox = previous.Inbox;
            var outbox = previous.Outbox;
            var inboxHandle = previous.Handle(false);
            var outboxHandle = previous.Handle(true);
            await previous.DisposeAsync();
            var current = await OpenAsync(previous.Provider, previous.Id);
            Assert.NotSame(manager, current.Manager);
            Assert.NotSame(effects, current.Effects);
            Assert.NotSame(inbox, current.Inbox);
            Assert.NotSame(outbox, current.Outbox);
            Assert.NotSame(inboxHandle, current.Handle(false));
            Assert.NotSame(outboxHandle, current.Handle(true));
            return current;
        }

        public async Task AssertDrainOccupiedAsync(int shards)
        {
            var inventory = await Services.GetRequiredService<IDurableJobsStorageInspector>().InspectAsync("A", Token);
            Assert.False(inventory.IsWriteProvider);
            Assert.Equal(shards, inventory.ShardCount);
            Assert.Equal(shards, inventory.OwnedShardCount);
        }

        public DurableEnvelope Envelope(Endpoint owner, bool outbox, Endpoint? sink = null) =>
            new DurableEnvelopeBuilder(Services.GetRequiredService<SerializerSessionPool>(), outbox ? owner.GrainId : GrainId.Create("external", "sender"))
                .To(outbox ? (sink?.GrainId ?? GrainId.Create("unused", "sink")) : owner.GrainId, "record")
                .WithBody("effect")
                .Build();

        public async Task AssertTerminalCallbackAsync(Endpoint endpoint, DurableJob job, bool outbox)
        {
            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(job);
            context.RunId.Returns(Guid.NewGuid().ToString("N"));
            var handler = endpoint.Handler(outbox);
            Assert.Equal(DurableJobRunStatus.Completed, (await handler.ExecuteJobAsync(context, Token)).Status);
            Assert.Equal(DurableJobRunStatus.Completed, (await handler.ExecuteJobAsync(context, Token)).Status);
        }

        public async Task DrainAsync()
        {
            // Each shard enumerator supplies the persisted handle and attempt context after replay.
            for (var pass = 0; pass < 10; pass++)
            {
                foreach (var shard in Jobs.Shards)
                {
                    var count = await shard.GetJobCountAsync();
                    await using var reader = shard.ConsumeDurableJobsAsync().GetAsyncEnumerator(Token);
                    for (var index = 0; index < count; index++)
                    {
                        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
                        var context = reader.Current;
                        Assert.Equal(DurableJobMutationResult.Applied, await shard.TryStartAttemptAsync(context, Token));
                        var endpoint = _endpoints[context.Job.TargetGrainId];
                        var handler = endpoint.Handler(context.Job.Name == JobName(true));
                        var result = await handler.ExecuteJobAsync(context, Token);
                        var duplicate = await handler.ExecuteJobAsync(context, Token);
                        Assert.Equal(result.Status, duplicate.Status);
                        for (var poll = 0; result.IsInProgress && poll < 10; poll++)
                        {
                            await RunTimersAsync();
                            result = await handler.ExecuteJobAsync(context, Token);
                        }

                        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
                        Assert.Equal(DurableJobRunStatus.Completed, (await handler.ExecuteJobAsync(context, Token)).Status);
                        Assert.Equal(DurableJobMutationResult.Applied, await shard.RemoveJobAsync(context.Job.Id, Token));
                        Assert.True(TerminalJobs.Add((context.Job.Id, context.Job.ShardId)));
                    }
                }

                await RunTimersAsync();
                if (TerminalJobs.Count == Jobs.Scheduled.Count)
                {
                    return;
                }
            }

            Assert.Fail("Durable jobs did not reach terminal cleanup within ten deterministic sweeps.");
        }

        private async Task RunTimersAsync()
        {
            foreach (var endpoint in _endpoints.Values) await endpoint.Timers.RunAsync();
            while (_deliveries.TryDequeue(out var delivery)) await delivery.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        public async Task AssertRetiredAsync()
        {
            foreach (var shard in Jobs.Shards)
            {
                Assert.Equal(0, await shard.GetJobCountAsync());
                await _manager.UnregisterShardAsync(shard, Token);
            }
            Jobs.Shards.Clear();
            var status = await Services.GetRequiredService<IDurableJobsStorageInspector>().InspectAsync("A", Token);
            Assert.False(status.IsWriteProvider);
            Assert.Equal(0, status.ShardCount);
            Assert.Equal(0, status.OwnedShardCount);
            Assert.Equal(0, status.PoisonedShardCount);
            Assert.Equal(0, status.UnrecognizedShardCount);
            await foreach (var entry in A.ListAsync(new JournalCatalogListOptions { Prefix = JournalId.Create("jobs", "shards") }, Token))
            {
                Assert.Fail($"Retired provider retained job shard {entry.Id}.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var endpoint in _openedEndpoints) await endpoint.DisposeAsync();
            foreach (var shard in Jobs.Shards) await shard.DisposeAsync();
            foreach (var provider in _providers) await provider.DisposeAsync();
        }
    }

    private sealed class EndpointBinding
    {
        public string Provider { get; set; } = null!;
        public JournalId Id { get; set; }
        public IGrainContext Context { get; } = CreateContext();

        private static IGrainContext CreateContext()
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainInstance.Returns(new object());
            context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
            return context;
        }
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private bool _disposed;
        public Endpoint(AsyncServiceScope scope, GrainId grainId, string provider, JournalId id)
        {
            _scope = scope;
            GrainId = grainId;
            Provider = provider;
            Id = id;
            var services = scope.ServiceProvider;
            Manager = services.GetRequiredService<IJournaledStateManager>();
            Fault = new FaultTrackingEffects(Manager);
            Manager.RegisterStateMachine("effects", Fault);
            Inbox = services.GetRequiredService<IDurableInbox>();
            Outbox = services.GetRequiredService<IDurableOutbox>();
            InboxExtension = (IDurableInboxExtension)services.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
            Timers = services.GetRequiredService<ManualTimers>();
            Effects = Fault;
            Inbox.RegisterHandler("record", new RecordHandler(Effects));
        }

        public string Provider { get; }
        public JournalId Id { get; }
        public FaultTrackingEffects Fault { get; }
        public GrainId GrainId { get; }
        public IJournaledStateManager Manager { get; }
        public IDurableInbox Inbox { get; }
        public IDurableOutbox Outbox { get; }
        public IDurableInboxExtension InboxExtension { get; }
        public IGrainExtension ResolvePublicInboxExtension() => _scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
        public ManualTimers Timers { get; }
        public IDurableDictionary<Guid, int> Effects { get; }
        public IDurableValue<DurableJob> Handle(bool outbox) => _scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<DurableJob>>(
            outbox ? "__orleans.durable-messaging.outbox-job-handle" : "__orleans.durable-messaging.inbox-job-handle");
        public IDurableValue<string> Generation(bool outbox) => _scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<string>>(
            outbox ? "__orleans.durable-messaging.outbox-job-id" : "__orleans.durable-messaging.inbox-job-id");
        public int Count(bool outbox) => outbox ? Outbox.Count : Inbox.Count;
        public IEnumerable<DurableEnvelope> Messages(bool outbox) => outbox ? Outbox.Messages : Inbox.Messages;
        public IDurableJobFeatureHandler Handler(bool outbox) => outbox ? Assert.IsAssignableFrom<IDurableJobFeatureHandler>(Outbox) : Assert.IsAssignableFrom<IDurableJobFeatureHandler>(InboxExtension);

        public DurableJob AssertOwner(bool outbox)
        {
            var handle = Assert.IsType<DurableJob>(Handle(outbox).Value);
            Assert.False(string.IsNullOrEmpty(Generation(outbox).Value));
            Assert.Equal(Generation(outbox).Value, handle.Metadata![OwnershipKey]);
            Assert.Equal(GrainId, handle.TargetGrainId);
            Assert.Equal(JobName(outbox), handle.Name);
            return handle;
        }

        public async Task AssertFencedAsync()
        {
            var failure = await Fault.Failure.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.IsType<IOException>(failure);
            var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() => Manager.WriteStateAsync(Token).AsTask());
            Assert.Same(failure, fenced.InnerException);
            Assert.Equal(1, Fault.FailureCount);
            var callback = Substitute.For<IJobRunContext>();
            callback.Job.Returns(new DurableJob { Id = "post-fault", ShardId = "post-fault", Name = JobName(true), TargetGrainId = GrainId });
            callback.RunId.Returns("post-fault");
            foreach (var outbox in new[] { false, true })
            {
                var rejected = await Assert.ThrowsAsync<IOException>(() => Handler(outbox).ExecuteJobAsync(callback, Token).AsTask());
                Assert.Same(failure, rejected);
            }
        }

        public async Task CommitAsync(DurableEnvelope envelope, bool outbox)
        {
            if (outbox)
            {
                using var batch = await Outbox.PrepareSendAsync([envelope], Token);
                Outbox.Send(batch);
                await Manager.WriteStateAsync(Token);
            }
            else
            {
                Assert.Equal(DeliveryStatus.Accepted, (await InboxExtension.DeliverAsync(envelope, Token)).Status);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Assert.IsAssignableFrom<ILifecycleObserver>(InboxExtension).OnStop(Token);
            await Assert.IsAssignableFrom<ILifecycleObserver>(Outbox).OnStop(Token);
            await _scope.DisposeAsync();
        }
    }

    private sealed class FaultTrackingEffects : IDurableDictionary<Guid, int>, IStateMachine, IDurableDictionaryCommandHandler<Guid, int>
    {
        private readonly Dictionary<Guid, int> _effects = [];
        private readonly IDurableDictionaryCommandCodec<Guid, int> _codec;
        private bool _dirty;

        public FaultTrackingEffects(IJournaledStateManager manager)
        {
            _codec = manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<Guid, int>>();
        }

        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FailureCount { get; private set; }
        public int Count => _effects.Count;
        public bool IsReadOnly => false;
        public ICollection<Guid> Keys => _effects.Keys;
        public ICollection<int> Values => _effects.Values;
        public int this[Guid key]
        {
            get => _effects[key];
            set
            {
                _effects[key] = value;
                _dirty = true;
            }
        }

        public void Add(Guid key, int value)
        {
            _effects.Add(key, value);
            _dirty = true;
        }

        public void Add(KeyValuePair<Guid, int> item) => Add(item.Key, item.Value);
        public void Clear()
        {
            _effects.Clear();
            _dirty = true;
        }

        public bool ContainsKey(Guid key) => _effects.ContainsKey(key);
        public bool TryGetValue(Guid key, out int value) => _effects.TryGetValue(key, out value);
        public bool Contains(KeyValuePair<Guid, int> item) => ((ICollection<KeyValuePair<Guid, int>>)_effects).Contains(item);
        public void CopyTo(KeyValuePair<Guid, int>[] array, int arrayIndex) => ((ICollection<KeyValuePair<Guid, int>>)_effects).CopyTo(array, arrayIndex);
        public bool Remove(Guid key)
        {
            var removed = _effects.Remove(key);
            _dirty |= removed;
            return removed;
        }

        public bool Remove(KeyValuePair<Guid, int> item) => Contains(item) && Remove(item.Key);
        public IEnumerator<KeyValuePair<Guid, int>> GetEnumerator() => _effects.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

        public void Reset(JournalStreamWriter writer)
        {
            _effects.Clear();
            _dirty = false;
        }

        public void WritePendingEntries(JournalStreamWriter writer)
        {
            if (_dirty)
            {
                WriteSnapshot(writer);
            }
        }

        public void WriteSnapshot(JournalStreamWriter writer)
        {
            _codec.WriteSnapshot(_effects, writer);
            _dirty = false;
        }

        void IDurableDictionaryCommandHandler<Guid, int>.ApplySet(Guid key, int value) => _effects[key] = value;
        void IDurableDictionaryCommandHandler<Guid, int>.ApplyRemove(Guid key) => _effects.Remove(key);
        void IDurableDictionaryCommandHandler<Guid, int>.ApplyClear() => _effects.Clear();
        void IDurableDictionaryCommandHandler<Guid, int>.Reset(int capacityHint)
        {
            _effects.Clear();
            _effects.EnsureCapacity(capacityHint);
        }

        public void OnFaulted(Exception exception)
        {
            FailureCount++;
            Failure.TrySetResult(exception);
        }
    }

    private sealed class RecordHandler(IDurableDictionary<Guid, int> effects) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;
        public ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            var id = context.Envelope.MessageId;
            var nextCount = effects.TryGetValue(id, out var count) ? count + 1 : 1;
            return new(() => effects[id] = nextCount);
        }
    }

    private sealed class ManualJobs : ILocalDurableJobManager
    {
        public IJobShard WriteShard { get; set; } = null!;
        public List<IJobShard> Shards { get; } = [];
        public List<DurableJob> Scheduled { get; } = [];
        public bool DuplicateNext { get; set; }
        public bool FailAfterNext { get; set; }

        public async Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            var job = Assert.IsType<DurableJob>(await WriteShard.TryScheduleJobAsync(request, cancellationToken));
            Scheduled.Add(job);
            if (DuplicateNext)
            {
                DuplicateNext = false;
                Scheduled.Add(Assert.IsType<DurableJob>(await WriteShard.TryScheduleJobAsync(request, cancellationToken)));
            }
            if (FailAfterNext)
            {
                FailAfterNext = false;
                throw new IOException("Injected lost schedule response after durable append.");
            }
            return job;
        }

        public async Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            await Shards.Single(shard => shard.Id == job.ShardId).RemoveJobAsync(job.Id, cancellationToken) == DurableJobMutationResult.Applied;
    }

    private sealed class ManualTimers(TimeProvider clock) : ITimerRegistry
    {
        private readonly Queue<Func<Task>> _callbacks = new();

        [Obsolete]
        public IDisposable RegisterTimer(IGrainContext grainContext, Func<object?, Task> callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The integration harness uses grain timers.");

        public IGrainTimer RegisterGrainTimer<TState>(IGrainContext grainContext, Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
        {
            var timer = Substitute.For<IGrainTimer>();
            var disposed = false;
            timer.When(value => value.Dispose()).Do(_ => disposed = true);
            var due = options.DueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + options.DueTime;
            timer.When(value => value.Change(Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>())).Do(call =>
                due = call.ArgAt<TimeSpan>(0) == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + call.ArgAt<TimeSpan>(0));
            _callbacks.Enqueue(RunAsync);
            return timer;

            Task RunAsync()
            {
                if (disposed) return Task.CompletedTask;
                if (due > clock.GetUtcNow())
                {
                    _callbacks.Enqueue(RunAsync);
                    return Task.CompletedTask;
                }

                return callback(state, Token);
            }
        }

        public async Task RunAsync()
        {
            var count = _callbacks.Count;
            for (var index = 0; index < count; index++) await _callbacks.Dequeue()().WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
