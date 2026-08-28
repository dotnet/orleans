using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableInboxRecoveryTests
{
    [Fact]
    public async Task RecoveryWithMessagesAndNoOwnerPersistsReplacementJob()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var sender = GrainId.Create("sender", "one");
        var receiver = GrainId.Create("receiver", "one");
        var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = sender,
            ReceiverId = receiver,
            RouteKey = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            Data = (DurableEnvelopeData)RuntimeHelpers.GetUninitializedObject(typeof(DurableEnvelopeData)),
        };
        var inboxState = new TestDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope>
        {
            [(sender, envelope.MessageId)] = envelope,
        };
        var jobId = new TestDurableValue<string>();
        var manager = new RecordingStateManager();
        var timers = new ImmediateTimerRegistry();
        var jobs = new RecordingJobManager(failuresBeforeSuccess: 1);
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(receiver);
        context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
        var inbox = new DurableInbox(inboxState);

        var extension = new DurableInboxExtension(
            context,
            Substitute.For<IGrainFactory>(),
            timers,
            manager,
            services.GetRequiredService<SerializerSessionPool>(),
            NullLogger<DurableInboxExtension>.Instance,
            DurableMessagingInstruments.CreateForDirectConstruction(),
            inbox,
            inboxState,
            new Dictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset>(),
            new Dictionary<(GrainId SenderId, Guid MessageId), InboxMessageState>(),
            new Dictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter>(),
            jobId,
            new TestDurableValue<string>(),
            new TestDurableValue<long>(),
            Substitute.For<IDurableOutbox>(),
            jobs,
            Substitute.For<IDurableJobHandlerRegistry>(),
            new DurableMessagingPumpResults(),
            TimeProvider.System,
            TimeProvider.System,
            new DurableInboxOptions { BackpressureRetryDelay = TimeSpan.FromMilliseconds(1) });

        extension.OnRecoveryCompleted();
        await timers.WaitAsync();

        Assert.Equal(2, jobs.ScheduleCount);
        Assert.False(string.IsNullOrEmpty(jobId.Value));
        Assert.True(manager.WriteCount >= 1);
    }

    private sealed class RecordingStateManager : IJournaledStateManager
    {
        public int WriteCount { get; private set; }
        public void RegisterObserver(IJournaledStateObserver observer) { }
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            WriteCount++;
            return default;
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class ImmediateTimerRegistry : ITimerRegistry
    {
        private readonly List<Task> _callbacks = [];

        [Obsolete]
        public IDisposable RegisterTimer(
            IGrainContext grainContext,
            Func<object?, Task> callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => throw new NotSupportedException();

        public IGrainTimer RegisterGrainTimer<TState>(
            IGrainContext grainContext,
            Func<TState, CancellationToken, Task> callback,
            TState state,
            GrainTimerCreationOptions options)
        {
            _callbacks.Add(callback(state, CancellationToken.None));
            return Substitute.For<IGrainTimer>();
        }

        public async Task WaitAsync()
        {
            for (var index = 0; ; index++)
            {
                Task callback;
                lock (_callbacks)
                {
                    if (index >= _callbacks.Count)
                    {
                        return;
                    }

                    callback = _callbacks[index];
                }

                await callback;
            }
        }
    }

    private sealed class RecordingJobManager(int failuresBeforeSuccess) : ILocalDurableJobManager
    {
        public int ScheduleCount { get; private set; }

        public Task<DurableJob> ScheduleJobAsync(
            ScheduleJobRequest request,
            CancellationToken cancellationToken)
        {
            ScheduleCount++;
            if (ScheduleCount <= failuresBeforeSuccess)
            {
                throw new IOException("Expected transient scheduling failure.");
            }

            return Task.FromResult(new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test",
                Metadata = request.Metadata,
            });
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class TestDurableValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }

    private sealed class TestDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;
}
