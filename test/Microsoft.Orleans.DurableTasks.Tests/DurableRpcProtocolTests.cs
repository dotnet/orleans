using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Messaging;
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
    public void DefaultDiagnosticStatePreservesAbsentNullableMembers()
    {
        var serializer = CreateSerializer();
        var state = default(DurableTaskDiagnosticState);

        var roundTripped = serializer.Deserialize<DurableTaskDiagnosticState>(
            serializer.SerializeToArray(state));

        Assert.Null(roundTripped.Status);
        Assert.Null(roundTripped.Waiters);
        Assert.Equal("[, Created: , Completed: , Request: , Response: , Waiters: ]", roundTripped.ToString());
    }

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
    public void TaskIdsRoundTripThroughMessagingHierarchicalKeys()
    {
        var taskId = TaskId.Parse("workflow/child");

        global::Orleans.Messaging.HierarchicalKey? key = taskId.ToHierarchicalKey();

        Assert.NotNull(key);
        Assert.Equal("workflow/child", key.Value.ToString());
        Assert.Equal(taskId, key.ToTaskId());
        Assert.Null(TaskId.None.ToHierarchicalKey());
        Assert.Equal(TaskId.None, ((global::Orleans.Messaging.HierarchicalKey?)null).ToTaskId());
        Assert.Equal(TaskId.None, ((global::Orleans.Messaging.HierarchicalKey?)default(global::Orleans.Messaging.HierarchicalKey)).ToTaskId());
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
        var jobs = new RecordingJobManager();
        var transport = MessagingTestHelpers.CreateTransport(new RecordingOutbox(), jobs);
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

    [Theory]
    [InlineData("caller", "target", "root", DurableTaskMessageTypes.InvocationSubject)]
    [InlineData("other-caller", "target", "root", DurableTaskMessageTypes.InvocationSubject)]
    [InlineData("caller", "other-target", "root", DurableTaskMessageTypes.InvocationSubject)]
    [InlineData("caller", "target", "root/child", DurableTaskMessageTypes.InvocationSubject)]
    [InlineData("caller", "target", "root", DurableTaskMessageTypes.CancellationSubject)]
    public void MessageIdIsStableForExactIdentityAndDistinctForChangedIdentity(
        string senderKey,
        string targetKey,
        string taskIdValue,
        string subject)
    {
        var baseline = DurableTaskMessageTransport.CreateMessageId(
            GrainId.Create("grain", "caller"),
            GrainId.Create("grain", "target"),
            TaskId.Parse("root"),
            DurableTaskMessageTypes.InvocationSubject);
        var candidate = DurableTaskMessageTransport.CreateMessageId(
            GrainId.Create("grain", senderKey),
            GrainId.Create("grain", targetKey),
            TaskId.Parse(taskIdValue),
            subject);

        Assert.Equal(
            senderKey == "caller"
                && targetKey == "target"
                && taskIdValue == "root"
                && subject == DurableTaskMessageTypes.InvocationSubject,
            baseline == candidate);
        Assert.Equal(
            baseline,
            DurableTaskMessageTransport.CreateMessageId(
                GrainId.Create("grain", "caller"),
                GrainId.Create("grain", "target"),
                TaskId.Parse("root"),
                DurableTaskMessageTypes.InvocationSubject));
    }

    [Theory]
    [InlineData(DurableTaskMessageTypes.InvocationSubject, "invoke")]
    [InlineData(DurableTaskMessageTypes.CompletionSubject, "complete")]
    [InlineData(DurableTaskMessageTypes.CompletionAckSubject, "complete-ack")]
    [InlineData(DurableTaskMessageTypes.CancellationSubject, "cancel")]
    public void MessageIdIsBoundedHierarchicalKeyScopedBySubject(string subject, string leaf)
    {
        var longTaskId = TaskId.Parse(string.Join('/', Enumerable.Repeat(new string('x', 200), 20)));

        var messageId = DurableTaskMessageTransport.CreateMessageId(
            GrainId.Create("caller", new string('c', 500)),
            GrainId.Create("target", "one"),
            longTaskId,
            subject);

        var segments = messageId.ToString().Split(HierarchicalKey.SegmentSeparator);
        Assert.Equal(3, segments.Length);
        Assert.Equal(DurableTaskMessageTransport.MessageIdRoot, segments[0]);
        Assert.Equal(leaf, segments[1]);
        Assert.Equal(64, segments[2].Length);
        Assert.All(segments[2], character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void CompletionAcknowledgementUsesDedicatedSubject()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");

        transport.SendCompletionAck(sender, target, TaskId.Parse("root"));

        var message = Assert.Single(outbox.Messages);
        Assert.Equal(DurableTaskMessageTypes.CompletionAckSubject, message.Envelope.Subject);
        Assert.Equal(target, message.ReceiverId);
        var body = MessagingTestHelpers.MessageTypes.CompletionAck.Decode(message);
        Assert.Equal(TaskId.Parse("root"), body.TaskId);
        Assert.Equal(sender, body.Sender);
    }

    [Fact]
    public void CompletionAcknowledgementReplayUsesStableMessageIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");

        transport.SendCompletionAck(sender, target, taskId);
        transport.SendCompletionAck(sender, target, taskId);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.Equal(messages[0].Envelope.Payload, messages[1].Envelope.Payload);
        Assert.Equal(
            DurableTaskMessageTransport.CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.CompletionAckSubject),
            messages[0].Envelope.MessageId);
    }

    [Fact]
    public void InvocationReplayUsesStableSubjectScopedMessageIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
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
        Assert.Equal(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.Equal(messages[0].Envelope.Payload, messages[1].Envelope.Payload);
        Assert.NotEqual(messages[0].Envelope.MessageId, messages[2].Envelope.MessageId);
        Assert.Equal(DurableTaskMessageTypes.InvocationSubject, messages[0].Envelope.Subject);
        Assert.Equal(target, messages[0].ReceiverId);
        var body = MessagingTestHelpers.MessageTypes.Invocation.Decode(messages[0]);
        Assert.Equal(taskId, body.TaskId);
        Assert.Equal(sender, body.Sender);
        var decodedRequest = Assert.IsType<TestDurableTaskRequest>(body.Request);
        Assert.Equal(sender, decodedRequest.Context!.CallerId);
        Assert.Equal(target, decodedRequest.Context.TargetId);
        Assert.True(decodedRequest.Context.SupportsDurableCompletion);
    }

    [Fact]
    public void CancellationReplayUsesStableSubjectScopedMessageIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("caller", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");

        transport.SendCancellation(sender, target, taskId);
        transport.SendCancellation(sender, target, taskId);
        transport.SendCompletionAck(sender, target, taskId);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.NotEqual(messages[0].Envelope.MessageId, messages[2].Envelope.MessageId);
        Assert.Equal(DurableTaskMessageTypes.CancellationSubject, messages[0].Envelope.Subject);
        var body = MessagingTestHelpers.MessageTypes.Cancellation.Decode(messages[0]);
        Assert.Equal(taskId, body.TaskId);
        Assert.Equal(sender, body.Sender);
    }

    [Fact]
    public void CompletionReplayUsesStableMessageIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("sender", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");
        var response = DurableTaskResponse.FromResult(42);

        transport.SendCompletion(sender, target, taskId, response);
        transport.SendCompletion(sender, target, taskId, response);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.Equal(messages[0].Envelope.Payload, messages[1].Envelope.Payload);
        var body = MessagingTestHelpers.MessageTypes.Completion.Decode(messages[0]);
        Assert.Equal(42, body.Response.GetResult<int>());
        Assert.Equal(sender, body.Sender);
    }

    [Fact]
    public void DefaultTaskIdIsRejectedBeforeStagingMessage()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);

        var exception = Assert.Throws<ArgumentException>(
            () => transport.SendCancellation(GrainId.Create("caller", "one"), GrainId.Create("target", "one"), default));

        Assert.Equal("taskId", exception.ParamName);
        Assert.Empty(outbox.Messages);
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
        silo.AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(2));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainRuntime));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(DurableTaskMessageHandler));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IInbox));
        Assert.Contains(silo.Services, descriptor => descriptor.ServiceType == typeof(IOutbox));
        Assert.Contains(
            silo.Services,
            descriptor => descriptor.ServiceType == typeof(IConfigureGrainTypeComponents)
                && descriptor.ImplementationType == typeof(DurableTaskGrainTypeConfigurator));
        using var provider = silo.Services.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.FromHours(2),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DurableTaskOptions>>().Value.ResultRetentionPeriod);
    }

    [Fact]
    public void SiloStartupRejectsJournalFormatWhichCannotStoreTaskState()
    {
        var silo = new TestSiloBuilder();
        silo.AddDurableTasks();
        using var provider = silo.Services.BuildServiceProvider();

        // Hosts run startup validators before starting the silo, so the default JSON format fails before any activation.
        var exception = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate());

        Assert.Equal("json", new JournaledStateManagerOptions().JournalFormatKey);
        var failure = Assert.Single(exception.Failures);
        Assert.Contains("requires the Orleans binary journal format", failure, StringComparison.Ordinal);
        Assert.Contains("JournaledStateManagerOptions.JournalFormatKey to \"orleans-binary\"", failure, StringComparison.Ordinal);

        var binarySilo = new TestSiloBuilder();
        binarySilo.AddDurableTasks();
        binarySilo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
        using var binaryProvider = binarySilo.Services.BuildServiceProvider();
        binaryProvider.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();
    }

    [Fact]
    public void VolatileTaskStorageIsSharedWithinAnActivationScope()
    {
        var silo = new TestSiloBuilder();
        silo.AddDurableTasks();
        silo.Services.RemoveAll<DurableTaskGrainStorage>();
        silo.Services.RemoveAll<IDurableTaskGrainStorage>();
        silo.AddVolatileDurableTaskStorage();
        using var provider = silo.Services.BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        var concrete = firstScope.ServiceProvider.GetRequiredService<VolatileDurableTaskGrainStorage>();
        var abstraction = firstScope.ServiceProvider.GetRequiredService<IDurableTaskGrainStorage>();
        Assert.Same(concrete, abstraction);

        using var secondScope = provider.CreateScope();
        Assert.NotSame(
            concrete,
            secondScope.ServiceProvider.GetRequiredService<VolatileDurableTaskGrainStorage>());
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
    public async Task PollAsync_TombstonedTask_ThrowsTypedNotFound()
    {
        var taskId = TaskId.Parse("root/expired-client-poll");
        var grain = new TombstoneResponseDurableTaskServer(taskId);
        var handle = new GrainScheduledTaskHandle(
            taskId,
            new RuntimeTestDurableTaskRequest(),
            grain,
            lastResponse: null);

        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => handle.PollAsync(
                new PollingOptions { PollTimeout = TimeSpan.Zero },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(taskId, failure.TaskId);
        Assert.Equal(
            $"Durable task '{taskId}' was not found or its retained state has expired.",
            failure.Message);
        Assert.Null(handle.LastResponse);
        Assert.Equal(1, grain.SubscribeOrPollCallCount);
        Assert.Equal(taskId, grain.LastRequestedTaskId);
        Assert.Equal(TimeSpan.Zero, grain.LastPollTimeout);
    }

    [Fact]
    public async Task WaitAsync_TombstonedTask_ThrowsTypedNotFoundWithoutRepolling()
    {
        var taskId = TaskId.Parse("root/expired-client-wait");
        var grain = new TombstoneResponseDurableTaskServer(taskId);
        var handle = new GrainScheduledTaskHandle(
            taskId,
            new RuntimeTestDurableTaskRequest(),
            grain,
            lastResponse: null);

        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => handle.WaitAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(taskId, failure.TaskId);
        Assert.Equal(
            $"Durable task '{taskId}' was not found or its retained state has expired.",
            failure.Message);
        Assert.Null(handle.LastResponse);
        Assert.Equal(1, grain.SubscribeOrPollCallCount);
        Assert.Equal(taskId, grain.LastRequestedTaskId);
        Assert.Equal(TimeSpan.FromSeconds(5), grain.LastPollTimeout);
    }

    [Fact]
    public async Task AttachedDurableTask_MissingTaskReturnsTypedNotFoundWithoutScheduling()
    {
        var taskId = TaskId.Parse("root/attached-missing");
        var grain = new TombstoneResponseDurableTaskServer(taskId);
        ScheduledTask<int> task = new AttachedScheduledTask<int>(taskId, grain);

        var failure = await Assert.ThrowsAsync<DurableTaskNotFoundException>(
            () => task.GetResponseAsync(
                new PollingOptions { PollTimeout = TimeSpan.Zero },
                TestContext.Current.CancellationToken));

        Assert.Equal(taskId, task.Id);
        Assert.Equal(taskId, failure.TaskId);
        Assert.Equal(0, grain.ScheduleCallCount);
        Assert.Equal(1, grain.SubscribeOrPollCallCount);
    }

    [Fact]
    public void GetDurableTaskRejectsNullTarget()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => DurableTaskGrainExtensions.GetDurableTask<int>(null!, "root"));

        Assert.Equal("target", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void GetDurableTaskRejectsEmptyOrWhitespaceRootId(string rootId)
    {
        var target = Substitute.For<IAddressable>();

        var exception = Assert.Throws<ArgumentException>(
            () => target.GetDurableTask<int>(rootId));

        Assert.Equal("rootId", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedDurableTask_StoredNotFoundFailureRemainsWorkflowFailure(
        bool useChildTaskId)
    {
        var rootId = TaskId.Parse("root/attached-failed");
        var failureTaskId = useChildTaskId
            ? TaskId.Parse("root/attached-failed/child")
            : rootId;
        var grain = new TombstoneResponseDurableTaskServer(
            rootId,
            failureTaskId,
            returnFailureResponse: true);
        ScheduledTask<int> task = new AttachedScheduledTask<int>(rootId, grain);

        var response = await task.GetResponseAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            TestContext.Current.CancellationToken);

        var failedResponse = Assert.IsType<ExceptionDurableTaskResponse>(response);
        var failure = Assert.IsType<DurableTaskNotFoundException>(failedResponse.Exception);
        Assert.Equal(rootId, task.Id);
        Assert.Equal(failureTaskId, failure.TaskId);
        Assert.Equal(0, grain.ScheduleCallCount);
        Assert.Equal(1, grain.SubscribeOrPollCallCount);
    }

    private static Serializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(DurableRpcProtocolTests).Assembly));
        return services.BuildServiceProvider().GetRequiredService<Serializer>();
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
