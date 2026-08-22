using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

        await transport.ScheduleResumeAsync(target, taskId, 7, dueTime, default);

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
        Assert.Equal(messages[0].CreatedAt, messages[1].CreatedAt);
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
        Assert.False((await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5))).CanBeCanceled);
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
        silo.AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(2));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainRuntime));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IInboxHandler));
        using var provider = silo.Services.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.FromHours(2),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DurableTaskOptions>>().Value.ResultRetentionPeriod);
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

        public Task<bool> TryCancelDurableJobAsync(DurableJob job, CancellationToken cancellationToken) =>
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
