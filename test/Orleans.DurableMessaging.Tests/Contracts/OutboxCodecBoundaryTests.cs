using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class OutboxCodecBoundaryTests
{
    [Fact]
    public async Task Send_ReusesEncodedBodyUntilActualJournalApplication()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));

        fixture.Outbox.Send(envelope);
        fixture.Outbox.Send(envelope);

        Assert.Equal(0, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(0, fixture.Probe.Count("OutboxMessageState"));
        Assert.Equal(0, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Outbox.Count);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(new[] { "finalization" }, fixture.Probe.Phases(nameof(DurableEnvelope)));
        Assert.Equal(new[] { "finalization" }, fixture.Probe.Phases("OutboxMessageState"));
        Assert.Equal(new[] { "capture" }, fixture.Probe.Phases(nameof(DurableJob)));
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Equal(envelope.MessageId, Assert.Single(fixture.Messages).Key);
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        var restored = Assert.Single(recovered.Messages).Value;
        Assert.True(restored.Data.TryGetBody<Payload>(out var payload));
        Assert.Equal(4096, Assert.IsType<Payload>(payload).Bytes.Length);
    }

    [Fact]
    public async Task DeadLetter_EncodesAtJournalApplicationWithoutPayloadPreflight()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.RouteNotFound("missing"));
        fixture.Outbox.Send(fixture.CreateEnvelope());
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();

        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(new[] { "finalization" }, fixture.Probe.Phases("OutboxDeadLetter"));
        Assert.Equal(1, fixture.Probe.Count("OutboxMessageState"));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.DeadLetterCount);
        Assert.Equal(2, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
    }

    [Fact]
    public async Task RetryState_EncodesOnceWhenApplied()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.Backpressured(), maxAttempts: 3);
        fixture.Outbox.Send(fixture.CreateEnvelope());
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.DeliverAsync();

        Assert.Equal(new[] { "finalization", "finalization" }, fixture.Probe.Phases("OutboxMessageState"));
        Assert.Equal(1, fixture.Probe.Count(nameof(Payload)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(1, fixture.Probe.Count(nameof(DurableJob)));
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.DeadLetterCount);
    }

    [Fact]
    public async Task BodyCodecFailure_IsReportedByBuilderBeforeIntentAdmission()
    {
        await using var fixture = await CodecFixture.CreateAsync();
        fixture.Probe.FailureType = nameof(Payload);
        Assert.Same(fixture.Probe.Failure, Assert.Throws<InvalidOperationException>(() => fixture.CreateEnvelope()));
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Equal(0, fixture.Probe.Count(nameof(DurableEnvelope)));
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Null(fixture.Observer.Failure);
    }

    [Theory]
    [InlineData(nameof(DurableEnvelope), "finalization", 0)]
    [InlineData("OutboxMessageState", "finalization", 1)]
    [InlineData(nameof(DurableJob), "capture", 1)]
    public async Task JournalCodecFailure_FencesActualManagerBeforePublishing(string failedType, string phase, int stagedMessages)
    {
        await using var fixture = await CodecFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        fixture.Probe.FailureType = failedType;
        fixture.Outbox.Send(envelope);
        Assert.Equal(0, fixture.Probe.Count(failedType));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(fixture.Probe.Failure, error);
        Assert.Same(error, fixture.Observer.Failure);
        Assert.Equal(new[] { phase }, fixture.Probe.Phases(failedType));
        Assert.Equal(stagedMessages, fixture.Messages.Count);
        Assert.Equal(0, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Send(envelope)));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(error, rejected.InnerException);

        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Empty(recovered.Messages);
        Assert.Null(recovered.Job.Value);
    }

    [Fact]
    public async Task DeadLetterCodecFailure_PreservesPreviouslyCommittedMessageOnFreshReplay()
    {
        await using var fixture = await CodecFixture.CreateAsync(delivery: DeliveryResult.RouteNotFound("missing"));
        var envelope = fixture.CreateEnvelope();
        fixture.Outbox.Send(envelope);
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        fixture.Probe.FailureType = "OutboxDeadLetter";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeliverAsync());
        Assert.Same(fixture.Probe.Failure, error);
        Assert.Same(error, fixture.Observer.Failure);
        Assert.Empty(fixture.Messages);
        Assert.Equal(new[] { "finalization" }, fixture.Probe.Phases("OutboxDeadLetter"));
        Assert.Equal(1, fixture.Storage.GetSuccessfulWriteCount(fixture.JournalId));
        await using var recovered = await CodecFixture.CreateAsync(fixture.Storage, fixture.JournalId);
        Assert.Equal(envelope.MessageId, Assert.Single(recovered.Messages).Key);
        Assert.Equal(0, recovered.DeadLetterCount);
    }

    [GenerateSerializer]
    public sealed record Payload([property: Id(0)] byte[] Bytes);

    public sealed class CodecProbe
    {
        private readonly ConcurrentQueue<(string Type, string Phase)> _calls = new();
        public required SerializerSessionPool InnerSessions { get; init; }
        public string Phase { get; set; } = "before-admission";
        public string? FailureType { get; set; }
        public InvalidOperationException Failure { get; } = new("Injected journal codec failure.");
        public void Writing(Type type)
        {
            _calls.Enqueue((type.Name, Phase));
            if (type.Name == FailureType)
            {
                throw Failure;
            }
        }
        public int Count(string type) => _calls.Count(call => call.Type == type);
        public string[] Phases(string type) => _calls.Where(call => call.Type == type).Select(static call => call.Phase).ToArray();
    }

    public sealed class CountingCodec<T>(CodecProbe probe) : IFieldCodec<T>
    {
        private readonly IFieldCodec<T> _inner = probe.InnerSessions.CodecProvider.GetCodec<T>();
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, [AllowNull] T value)
            where TBufferWriter : IBufferWriter<byte>
        {
            probe.Writing(typeof(T));
            _inner.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }
        [return: MaybeNull]
        public T ReadValue<TInput>(ref Reader<TInput> reader, Field field) => _inner.ReadValue(ref reader, field);
    }

    private sealed class CodecFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _innerServices;
        private readonly ServiceProvider _services;
        private readonly AsyncServiceScope _scope;
        private readonly Func<CancellationToken, Task> _deliver;
        public CodecProbe Probe { get; }
        public JournalId JournalId { get; }
        public ControlledJournalStorageProvider Storage { get; }
        public IJournaledStateManager Manager { get; }
        public IDurableOutbox Outbox { get; }
        public IDurableDictionary<Guid, DurableEnvelope> Messages { get; }
        public IDurableValue<DurableJob> Job { get; }
        public EndpointObserver Observer { get; }
        public int DeadLetterCount
        {
            get
            {
                var entries = _scope.ServiceProvider.GetRequiredKeyedService(
                    typeof(IDurableDictionary<,>).MakeGenericType(typeof(Guid), Implementation("OutboxDeadLetter")),
                    "__orleans.durable-messaging.outbox-dead-letters");
                return (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
            }
        }

        private CodecFixture(ControlledJournalStorageProvider? storage, JournalId? journalId, DeliveryResult delivery, int maxAttempts)
        {
            JournalId = journalId ?? new JournalId($"codec-boundary/{Guid.NewGuid():N}");
            Storage = storage ?? new ControlledJournalStorageProvider();
            Storage.Configure(Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "orleans-binary" }));
            _innerServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
            Probe = new CodecProbe { InnerSessions = _innerServices.GetRequiredService<SerializerSessionPool>() };
            var services = new ServiceCollection();
            services.AddSerializer(builder => builder.Configure(options =>
            {
                options.AddFieldCodec(typeof(CountingCodec<Payload>));
                options.AddFieldCodec(typeof(CountingCodec<DurableEnvelope>));
                options.AddFieldCodec(typeof(CountingCodec<DurableJob>));
                options.AddFieldCodec(typeof(CountingCodec<>).MakeGenericType(Implementation("OutboxMessageState")));
                options.AddFieldCodec(typeof(CountingCodec<>).MakeGenericType(Implementation("OutboxDeadLetter")));
            }));
            services.AddSingleton(Probe);
            services.AddLogging();
            services.AddKeyedSingleton(JournalingTimeProviderNames.Journaling, TimeProvider.System);
            services.AddKeyedSingleton(DurableJobTimeProviderNames.DurableJobs, TimeProvider.System);
            services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
            var silo = Substitute.For<ISiloBuilder>();
            silo.Services.Returns(services);
            silo.AddJournalStorage();
            services.AddSingleton<IJournalStorageProvider>(Storage);
            services.AddScoped(sp => sp.GetRequiredService<IJournaledStateManagerFactory>().Create(JournalId));
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(GrainId.Create("sender", "codec-boundary"));
            context.GrainInstance.Returns(new object());
            context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
            services.AddSingleton(context);
            services.AddSingleton(Substitute.For<ITimerRegistry>());
            services.AddSingleton(Substitute.For<IDurableJobHandlerRegistry>());
            var jobs = Substitute.For<ILocalDurableJobManager>();
            jobs.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var request = call.ArgAt<ScheduleJobRequest>(0);
                return Task.FromResult(new DurableJob
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ShardId = "opaque-shard",
                    Name = request.JobName,
                    DueTime = request.DueTime,
                    TargetGrainId = request.Target,
                    Metadata = request.Metadata
                });
            });
            services.AddSingleton(jobs);
            var inbox = Substitute.For<IDurableInboxExtension>();
            inbox.DeliverAsync(Arg.Any<DurableEnvelope>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(delivery));
            var grains = Substitute.For<IGrainFactory>();
            grains.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>()).Returns(inbox);
            services.AddSingleton(grains);
            services.AddSingleton(Implementation("DurableMessagingInstruments"), Implementation("DurableMessagingInstruments")
                .GetMethod("CreateForDirectConstruction", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);
            services.AddScoped(Implementation("DurableMessagingPumpResults"), _ => Activator.CreateInstance(Implementation("DurableMessagingPumpResults"), nonPublic: true)!);
            services.Configure<DurableInboxOptions>(options => options.MaxDeliveryAttempts = maxAttempts);
            _services = services.BuildServiceProvider();
            _scope = _services.CreateAsyncScope();
            Manager = _scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            Outbox = (IDurableOutbox)ActivatorUtilities.CreateInstance(_scope.ServiceProvider, Implementation("DurableOutbox"));
            var finalize = Outbox.GetType().GetMethod("FinalizeWrite")!.CreateDelegate<Action<CancellationToken>>(Outbox);
            var endpoint = Activator.CreateInstance(Implementation("DurableMessagingJournalEndpoint"), (IJournaledStateObserver)Outbox, finalize)!;
            Observer = new EndpointObserver((IJournaledStateObserver)endpoint.GetType().GetProperty("Observer")!.GetValue(endpoint)!,
                endpoint.GetType().GetMethod("FinalizeWrite")!.CreateDelegate<Action<CancellationToken>>(endpoint), Probe);
            Manager.RegisterObserver(Observer);
            Messages = _scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("__orleans.durable-messaging.outbox");
            Job = _scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle");
            _deliver = Outbox.GetType().GetMethod("DeliverPendingMessagesAsync")!.CreateDelegate<Func<CancellationToken, Task>>(Outbox);
        }

        public static async Task<CodecFixture> CreateAsync(ControlledJournalStorageProvider? storage = null, JournalId? journalId = null,
            DeliveryResult? delivery = null, int maxAttempts = 1)
        {
            var result = new CodecFixture(storage, journalId, delivery ?? DeliveryResult.Accepted(), maxAttempts);
            await result.Manager.InitializeAsync(TestContext.Current.CancellationToken);
            return result;
        }
        public DurableEnvelope CreateEnvelope() => new DurableEnvelopeBuilder(_scope.ServiceProvider.GetRequiredService<SerializerSessionPool>(), GrainId.Create("sender", "codec-boundary"))
            .To(GrainId.Create("receiver", "codec-boundary"), "codec").WithBody(new Payload(new byte[4096])).Build();
        public Task DeliverAsync() => _deliver(TestContext.Current.CancellationToken);
        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
            await _innerServices.DisposeAsync();
        }
        private static Type Implementation(string name) => ReceiverTestServices.GetImplementationType(name);
    }

    private sealed class EndpointObserver(IJournaledStateObserver observer, Action<CancellationToken> finalize, CodecProbe probe) : IJournaledStateObserver
    {
        public Exception? Failure { get; private set; }
        public void OnWriteRequested() => observer.OnWriteRequested();
        public async ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
        {
            probe.Phase = "preparation";
            await observer.OnWritePreparingAsync(cancellationToken);
        }
        public ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken)
        {
            probe.Phase = "finalization";
            finalize(cancellationToken);
            return default;
        }
        public void OnWriteStarted() { probe.Phase = "capture"; observer.OnWriteStarted(); }
        public void OnWriteCompleted() { observer.OnWriteCompleted(); probe.Phase = "before-admission"; }
        public void OnRecoveryStarted() => observer.OnRecoveryStarted();
        public void OnRecoveryCompleted() => observer.OnRecoveryCompleted();
        public void OnFaulted(Exception exception) { Failure = exception; observer.OnFaulted(exception); }
    }
}
