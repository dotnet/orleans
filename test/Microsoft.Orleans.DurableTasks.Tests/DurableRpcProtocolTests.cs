using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableRpcProtocolTests
{
    [Fact]
    public void DurableTaskExtensionOperationsDoNotAlwaysInterleaveWithGrainTurns()
    {
        var methods = typeof(IDurableTaskServer).GetMethods()
            .Concat(typeof(IDurableTaskObserver).GetMethods());

        Assert.All(
            methods,
            method => Assert.Null(method.GetCustomAttribute<AlwaysInterleaveAttribute>()));
    }

    [Fact]
    public void OrleansSerializationFingerprintIncludesPrivateStateAndIsStableForEquivalentGraphs()
    {
        var serializer = CreateSerializer();
        var firstGraph = new FingerprintArgument(42);
        firstGraph.Next = firstGraph;
        var retryGraph = new FingerprintArgument(42);
        retryGraph.Next = retryGraph;
        var conflictGraph = new FingerprintArgument(43);
        conflictGraph.Next = conflictGraph;
        var first = new RuntimeTestDurableTaskRequest(
            interfaceName: "ITestGrain",
            methodName: "Run",
            arguments: [firstGraph, firstGraph]);
        var retry = new RuntimeTestDurableTaskRequest(
            interfaceName: "ITestGrain",
            methodName: "Run",
            arguments: [retryGraph, retryGraph]);
        var conflict = new RuntimeTestDurableTaskRequest(
            interfaceName: "ITestGrain",
            methodName: "Run",
            arguments: [conflictGraph, conflictGraph]);

        var firstFingerprint = IDurableTaskRequest.GetFingerprint(first, serializer);
        Assert.Equal(64, firstFingerprint.Length);
        Assert.Equal(firstFingerprint, IDurableTaskRequest.GetFingerprint(retry, serializer));
        Assert.Equal(firstFingerprint, IDurableTaskRequest.GetFingerprint(first, serializer));
        Assert.NotEqual(firstFingerprint, IDurableTaskRequest.GetFingerprint(conflict, serializer));
    }

    [Fact]
    public void GeneratedRequestAliasesProvideStableDistinctMethodIdentity()
    {
        var identities = typeof(IDurableRpcCodegenGrain).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(IDurableTaskRequest).IsAssignableFrom(type))
            .Select(RuntimeTypeNameFormatter.Format)
            .ToArray();

        Assert.True(identities.Length >= 2);
        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identities, identity => Assert.DoesNotContain("Version=", identity, StringComparison.Ordinal));
    }

    [Fact]
    public void TaskIdsRoundTripThroughDurableMessagingCorrelationKeys()
    {
        var taskId = TaskId.Parse("workflow/child");

        global::Orleans.DurableMessaging.HierarchicalKey? correlationKey = taskId.ToHierarchicalKey();

        Assert.NotNull(correlationKey);
        Assert.Equal(taskId, correlationKey.ToTaskId());
        Assert.Equal(TaskId.None, ((global::Orleans.DurableMessaging.HierarchicalKey?)null).ToTaskId());
    }

    [Fact]
    public void ClientSchedulingClearsUntrustedCallerIdentity()
    {
        var context = new DurableTaskRequestContext
        {
            CallerId = GrainId.Create("forged", "caller"),
            TargetId = GrainId.Create("target", "one"),
            SupportsDurableCompletion = true,
        };

        DurableTaskRequest.PrepareClientContext(context);

        Assert.Equal(default, context.CallerId);
        Assert.False(context.SupportsDurableCompletion);
    }

    [Fact]
    public void CompletionWaiterRemainsUntilAcknowledgedAndTombstoneRetainsIdentity()
    {
        var destination = GrainId.Create("caller", "one");
        var state = new DurableTaskState
        {
            RequestFingerprint = "fingerprint",
            Result = DurableTaskResponse.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            CompletionDestinations = [destination],
        };

        Assert.Contains(destination, state.CompletionDestinations);
        state.CompletionDestinations.Remove(destination);
        state.Request = null;
        state.Result = null;
        state.TombstonedAt = DateTimeOffset.UtcNow;

        Assert.Empty(state.CompletionDestinations);
        Assert.Equal("fingerprint", state.RequestFingerprint);
        Assert.NotNull(state.TombstonedAt);
    }

    [Fact]
    public async Task ResumeJobCarriesTaskIdentityAndGeneration()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var jobs = new RecordingJobManager();
        var transport = new DurableTaskMessageTransport(
            new RecordingOutbox(),
            jobs,
            services.GetRequiredService<SerializerSessionPool>());
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root/delay");
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);

        await transport.ScheduleResumeAsync(target, taskId, 7, dueTime, TestContext.Current.CancellationToken);

        var request = Assert.Single(jobs.Requests);
        Assert.Equal(DurableTaskMessageTransport.ResumeJobName, request.JobName);
        Assert.Equal(target, request.Target);
        Assert.Equal(dueTime, request.DueTime);
        Assert.Equal(taskId.ToString(), request.Metadata![DurableTaskMessageTransport.ResumeTaskIdMetadata]);
        Assert.Equal("7", request.Metadata[DurableTaskMessageTransport.ResumeGenerationMetadata]);
    }

    [Fact]
    public void CompletionAcknowledgementUsesDedicatedDurableRoute()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var outbox = new RecordingOutbox();
        var transport = new DurableTaskMessageTransport(
            outbox,
            new RecordingJobManager(),
            services.GetRequiredService<SerializerSessionPool>());

        transport.SendCompletionAck(
            GrainId.Create("caller", "one"),
            GrainId.Create("target", "one"),
            TaskId.Parse("root"));

        var envelope = Assert.Single(outbox.Messages);
        Assert.Equal(DurableTaskMessageTransport.CompletionAckRoute, envelope.RouteKey);
        Assert.True(envelope.Data.TryGetBody<DurableTaskCompletionAckMessage>(out var body));
        Assert.Equal(TaskId.Parse("root"), body!.TaskId);
    }

    [Fact]
    public void CompletionAcknowledgementReplayUsesStableMessageIdentity()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var outbox = new RecordingOutbox();
        var transport = new DurableTaskMessageTransport(
            outbox,
            new RecordingJobManager(),
            services.GetRequiredService<SerializerSessionPool>());
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");

        transport.SendCompletionAck(sender, target, taskId);
        transport.SendCompletionAck(sender, target, taskId);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(messages[0].MessageId, messages[1].MessageId);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[0].CreatedAt);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[1].CreatedAt);
    }

    [Fact]
    public void InvocationReplayUsesStableRouteScopedMessageIdentity()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var outbox = new RecordingOutbox();
        var transport = new DurableTaskMessageTransport(
            outbox,
            new RecordingJobManager(),
            services.GetRequiredService<SerializerSessionPool>());
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");
        var request = new TestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext
            {
                CallerId = sender,
                TargetId = target,
                SupportsDurableCompletion = true,
            },
        };

        transport.SendInvocation(sender, target, taskId, request);
        transport.SendInvocation(sender, target, taskId, request);
        transport.SendCancellation(sender, target, taskId);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal(messages[0].MessageId, messages[1].MessageId);
        Assert.NotEqual(messages[0].MessageId, messages[2].MessageId);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[0].CreatedAt);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[1].CreatedAt);
        Assert.Equal(DurableTaskMessageTransport.InvocationRoute, messages[0].RouteKey);
        Assert.True(messages[0].Data.TryGetBody<DurableTaskInvocationMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
        var decodedRequest = Assert.IsType<TestDurableTaskRequest>(body.Request);
        Assert.Equal(sender, decodedRequest.Context!.CallerId);
        Assert.Equal(target, decodedRequest.Context.TargetId);
        Assert.True(decodedRequest.Context.SupportsDurableCompletion);
    }

    [Fact]
    public void CancellationReplayUsesStableRouteScopedMessageIdentity()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var outbox = new RecordingOutbox();
        var transport = new DurableTaskMessageTransport(
            outbox,
            new RecordingJobManager(),
            services.GetRequiredService<SerializerSessionPool>());
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");

        transport.SendCancellation(sender, target, taskId);
        transport.SendCancellation(sender, target, taskId);
        transport.SendCompletionAck(sender, target, taskId);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal(messages[0].MessageId, messages[1].MessageId);
        Assert.NotEqual(messages[0].MessageId, messages[2].MessageId);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[0].CreatedAt);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[1].CreatedAt);
        Assert.Equal(DurableTaskMessageTransport.CancellationRoute, messages[0].RouteKey);
        Assert.True(messages[0].Data.TryGetBody<DurableTaskCancellationMessage>(out var body));
        Assert.Equal(taskId, body!.TaskId);
    }

    [Fact]
    public void CompletionReplayUsesStableMessageIdentity()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskMessageTransport).Assembly))
            .BuildServiceProvider();
        var outbox = new RecordingOutbox();
        var transport = new DurableTaskMessageTransport(
            outbox,
            new RecordingJobManager(),
            services.GetRequiredService<SerializerSessionPool>());
        var sender = GrainId.Create("sender", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");
        var response = DurableTaskResponse.FromResult(42);

        transport.SendCompletion(sender, target, taskId, response);
        transport.SendCompletion(sender, target, taskId, response);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(messages[0].MessageId, messages[1].MessageId);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[0].CreatedAt);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, messages[1].CreatedAt);
        Assert.Equal(Copy(messages[0].Data.GetBodyBytes()), Copy(messages[1].Data.GetBodyBytes()));

        static byte[] Copy(ReadOnlySequence<byte> sequence)
        {
            var result = new byte[sequence.Length];
            sequence.CopyTo(result);
            return result;
        }
    }

    [Fact]
    public async Task CancelAbandonsWaitWithoutCancelingRemoteSubmission()
    {
        var request = new RuntimeTestDurableTaskRequest();
        var grain = Substitute.For<IDurableTaskServer>();
        var submitted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        grain.CancelAsync(Arg.Any<TaskId>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                submitted.TrySetResult(call.ArgAt<CancellationToken>(1));
                return new ValueTask(completion.Task);
            });
        var handle = new GrainScheduledTaskHandle(
            TaskId.Parse("root"),
            request,
            grain,
            lastResponse: null);
        using var cancellation = new CancellationTokenSource();

        var cancel = handle.CancelAsync(cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancel);
        Assert.False((await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).CanBeCanceled);
        completion.TrySetResult();
    }

    [Fact]
    public void AdapterAssemblyRegistersCustomDurableTaskReturnMappings()
    {
        var mappings = typeof(DurableTaskRequest).Assembly
            .GetCustomAttributes(typeof(InvokableBaseTypeAttribute), inherit: false)
            .Cast<InvokableBaseTypeAttribute>()
            .ToArray();

        Assert.Contains(mappings, mapping => mapping.ReturnType == typeof(DurableTask));
        Assert.Contains(mappings, mapping => mapping.ReturnType == typeof(DurableTask<>));
    }

    [Fact]
    public void StandaloneDurableTaskSerializationFailsWithClearError()
    {
        IConverter<DurableTask, DurableTaskSurrogate> converter = new DurableTaskPopulator();

        var exception = Assert.Throws<NotSupportedException>(
            () => converter.ConvertToSurrogate(DurableTask.Delay(TimeSpan.FromSeconds(1))));

        Assert.Contains("cannot be serialized directly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneGenericDurableTaskSerializationFailsWithClearError()
    {
        IConverter<DurableTask<int>, DurableTaskSurrogate> converter = new DurableTaskPopulator<int>();

        var exception = Assert.Throws<NotSupportedException>(
            () => converter.ConvertToSurrogate(DurableTask.FromResult(42)));

        Assert.Contains("cannot be serialized directly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Obsolete]
    public void HostingIsOptInAndRegistersClientAndSiloAdapters()
    {
        var client = new TestClientBuilder();
        Assert.DoesNotContain(client.Services, descriptor => descriptor.ServiceType == typeof(DurableTaskRequestShared));
        client.AddDurableTasks();
        Assert.Contains(client.Services, descriptor => descriptor.ServiceType == typeof(DurableTaskRequestShared));

        var silo = new TestSiloBuilder();
        Assert.DoesNotContain(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainRuntime));
        silo.AddDurableTasks(options =>
        {
            options.ResultRetentionPeriod = TimeSpan.FromHours(2);
            options.RecoveryExecutionDrainTimeout = TimeSpan.FromSeconds(5);
        });
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainRuntime));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IInboxHandler));
        using var provider = silo.Services.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.FromHours(2),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DurableTaskOptions>>().Value.ResultRetentionPeriod);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DurableTaskOptions>>().Value.RecoveryExecutionDrainTimeout);
    }

    [Fact]
    public void CodeGeneratorEmitsDurableTaskRequestForMappedReturnType()
    {
        var generated = typeof(IDurableRpcCodegenGrain).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(DurableTaskRequest).IsAssignableFrom(type))
            .ToArray();

        Assert.NotEmpty(generated);
    }

    [Fact]
    public async Task PollAsync_TombstonedTask_ReturnsExpiredTerminalFailure()
    {
        var taskId = TaskId.Parse("root/expired-client-poll");
        var grain = new TombstoneResponseDurableTaskServer(taskId);
        var handle = new GrainScheduledTaskHandle(
            taskId,
            new RuntimeTestDurableTaskRequest(),
            grain,
            lastResponse: null);

        var response = await handle.PollAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            TestContext.Current.CancellationToken);

        var failedResponse = Assert.IsType<ExceptionDurableTaskResponse>(response);
        var failure = Assert.IsType<DurableTaskTerminalFailure>(failedResponse.Exception);
        Assert.True(failedResponse.IsCompleted);
        Assert.Equal(DurableTaskResponseKind.Failed, failedResponse.ResponseKind);
        Assert.Equal(DurableTaskStatus.Failed, failedResponse.Status);
        Assert.Equal(DurableTaskTerminalFailureCode.ExpiredOrTombstoned, failure.Code);
        Assert.Equal(taskId, failure.TaskId);
        Assert.Equal(
            $"Durable task '{taskId}' has expired and its result is no longer available.",
            failure.Message);
        Assert.Same(response, handle.LastResponse);
        Assert.Equal(1, grain.SubscribeOrPollCallCount);
        Assert.Equal(taskId, grain.LastRequestedTaskId);
        Assert.Equal(TimeSpan.Zero, grain.LastPollTimeout);
    }

    [Fact]
    public async Task WaitAsync_TombstonedTask_ReturnsExpiredTerminalFailureWithoutRepolling()
    {
        var taskId = TaskId.Parse("root/expired-client-wait");
        var grain = new TombstoneResponseDurableTaskServer(taskId);
        var handle = new GrainScheduledTaskHandle(
            taskId,
            new RuntimeTestDurableTaskRequest(),
            grain,
            lastResponse: null);

        var response = await handle.WaitAsync(TestContext.Current.CancellationToken);

        var failedResponse = Assert.IsType<ExceptionDurableTaskResponse>(response);
        var failure = Assert.IsType<DurableTaskTerminalFailure>(failedResponse.Exception);
        Assert.True(failedResponse.IsCompleted);
        Assert.Equal(DurableTaskResponseKind.Failed, failedResponse.ResponseKind);
        Assert.Equal(DurableTaskStatus.Failed, failedResponse.Status);
        Assert.Equal(DurableTaskTerminalFailureCode.ExpiredOrTombstoned, failure.Code);
        Assert.Equal(taskId, failure.TaskId);
        Assert.Equal(
            $"Durable task '{taskId}' has expired and its result is no longer available.",
            failure.Message);
        Assert.Same(response, handle.LastResponse);
        Assert.Equal(1, grain.SubscribeOrPollCallCount);
        Assert.Equal(taskId, grain.LastRequestedTaskId);
        Assert.Equal(TimeSpan.FromSeconds(5), grain.LastPollTimeout);
    }

    private sealed class RecordingOutbox : IDurableOutbox
    {
        private readonly List<DurableEnvelope> _messages = [];
        public int Count => _messages.Count;
        public IEnumerable<DurableEnvelope> Messages => _messages;
        public void Send(DurableEnvelope envelope) => _messages.Add(envelope);
        public bool TryGetMessage(Guid messageId, out DurableEnvelope envelope)
        {
            foreach (var candidate in _messages)
            {
                if (candidate.MessageId == messageId)
                {
                    envelope = candidate;
                    return true;
                }
            }

            envelope = default;
            return false;
        }
    }

    private static Serializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(DurableRpcProtocolTests).Assembly));
        return services.BuildServiceProvider().GetRequiredService<Serializer>();
    }

    private sealed class RecordingJobManager : ILocalDurableJobManager
    {
        public List<ScheduleJobRequest> Requests { get; } = [];

        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
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

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

[GenerateSerializer]
internal sealed class FingerprintArgument(int value)
{
    [Id(0)]
    private readonly int _value = value;

    [Id(1)]
    public FingerprintArgument? Next { get; set; }
}

public interface IDurableRpcCodegenGrain : IGrainWithStringKey
{
    DurableTask ExecuteAsync(int value);
    DurableTask<int> GetValueAsync();
}
