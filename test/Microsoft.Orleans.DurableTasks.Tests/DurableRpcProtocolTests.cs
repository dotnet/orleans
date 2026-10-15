using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
using Orleans.Messaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
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
        Assert.Equal("[, Created: , Completed: , Due: , Request: , Remote: , Response: , Waiters: ]", roundTripped.ToString());
    }

    [Fact]
    public void DurableTaskExtensionOperationsWhichChangeStateDoNotAlwaysInterleaveWithGrainTurns()
    {
        var poll = typeof(IDurableTaskServer).GetMethod(nameof(IDurableTaskServer.SubscribeOrPollAsync))!;
        var methods = typeof(IDurableTaskServer).GetMethods()
            .Concat(typeof(IDurableTaskObserver).GetMethods())
            .Where(method => method != poll);

        Assert.All(
            methods,
            method => Assert.Null(method.GetCustomAttribute<AlwaysInterleaveAttribute>()));

        // A poll changes no state and waits, so it interleaves with the turns which complete the task.
        Assert.NotNull(poll.GetCustomAttribute<AlwaysInterleaveAttribute>());
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
    public void CompletionWaiterRemainsUntilRetired()
    {
        var destination = new DurableTaskSubscriber(GrainId.Create("caller", "one"), TaskId.CreateRoot("root"));
        var serializer = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DurableTaskState).Assembly))
            .BuildServiceProvider()
            .GetRequiredService<Serializer>();
        var state = new DurableTaskState
        {
            RequestFingerprint = "fingerprint",
            CompletedAt = DateTimeOffset.UtcNow,
            CompletionDestinations = [destination],
        }.Bind(serializer);
        state.SetResult(DurableTaskResponse.Completed);

        Assert.Contains(destination, state.CompletionDestinations);
        state.CompletionDestinations.Remove(destination);

        Assert.Empty(state.CompletionDestinations);
        Assert.Equal("fingerprint", state.RequestFingerprint);
        Assert.NotNull(state.ResultPayload);
    }
    [Fact]
    public void ResumeIsScheduledSelfMessageCarryingTaskIdentityAndGeneration()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var grain = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root/delay");
        var dueTime = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);

        transport.SendResume(grain, taskId, 7, dueTime);
        transport.SendResume(grain, taskId, 7, dueTime);

        var messages = outbox.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        var message = messages[0];
        Assert.Equal(grain, message.ReceiverId);
        Assert.Equal(DurableTaskMessageTypes.ResumeSubject, message.Envelope.Subject);
        Assert.Equal(dueTime, message.Envelope.DeliverAt);
        var body = MessagingTestHelpers.MessageTypes.Resume.Decode(message.Envelope);
        Assert.Equal(taskId, body.TaskId);
        Assert.Equal(7, body.Generation);
        Assert.Equal(grain, body.Sender);

        // Replays coalesce in the grain's own inbox: the identity and content are stable for one delay generation.
        Assert.Equal(message.Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.Equal(message.Envelope.Payload, messages[1].Envelope.Payload);
        Assert.Equal(DurableTaskMessageTransport.CreateResumeMessageId(grain, taskId, 7, dueTime), message.Envelope.MessageId);
        var segments = message.Envelope.MessageId.ToString().Split(HierarchicalKey.SegmentSeparator);
        Assert.Equal(new[] { DurableTaskMessageTransport.MessageIdRoot, "resume" }, segments[..2]);
        Assert.Equal(64, segments[2].Length);
        Assert.Equal("7", segments[3]);
        Assert.Equal(4, segments.Length);

        Assert.NotEqual(message.Envelope.MessageId, DurableTaskMessageTransport.CreateResumeMessageId(grain, taskId, 8, dueTime));
        Assert.NotEqual(message.Envelope.MessageId, DurableTaskMessageTransport.CreateResumeMessageId(grain, taskId, 7, dueTime.AddTicks(1)));
        Assert.NotEqual(message.Envelope.MessageId, DurableTaskMessageTransport.CreateResumeMessageId(grain, TaskId.Parse("root/other"), 7, dueTime));
        Assert.NotEqual(message.Envelope.MessageId, DurableTaskMessageTransport.CreateResumeMessageId(GrainId.Create("target", "two"), taskId, 7, dueTime));
        Assert.Throws<ArgumentOutOfRangeException>(() => transport.SendResume(grain, taskId, 0, dueTime));
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
    [InlineData(DurableTaskMessageTypes.CancellationSubject, "cancel")]
    [InlineData(DurableTaskMessageTypes.ResumeSubject, "resume")]
    [InlineData(DurableTaskMessageTypes.SignalSubject, "signal")]
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
    public void DurableRpcSubjectsHaveNoCompletionAcknowledgement()
    {
        Assert.Equal(
            [
                DurableTaskMessageTypes.CancellationSubject,
                DurableTaskMessageTypes.CompletionSubject,
                DurableTaskMessageTypes.InvocationSubject,
                DurableTaskMessageTypes.ResumeSubject,
                DurableTaskMessageTypes.SignalSubject,
            ],
            typeof(DurableTaskMessageTypes)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(static field => field.IsLiteral && field.Name.EndsWith("Subject", StringComparison.Ordinal))
                .Select(static field => (string)field.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal));
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
        var body = MessagingTestHelpers.MessageTypes.Invocation.Decode(messages[0].Envelope);
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
        transport.SendInvocation(sender, target, taskId, new TestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext { CallerId = sender, TargetId = target, SupportsDurableCompletion = true },
        });

        var messages = outbox.Messages.ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal(messages[0].Envelope.MessageId, messages[1].Envelope.MessageId);
        Assert.NotEqual(messages[0].Envelope.MessageId, messages[2].Envelope.MessageId);
        Assert.Equal(DurableTaskMessageTypes.CancellationSubject, messages[0].Envelope.Subject);
        var body = MessagingTestHelpers.MessageTypes.Cancellation.Decode(messages[0].Envelope);
        Assert.Equal(taskId, body.TaskId);
        Assert.Equal(sender, body.Sender);
    }

    [Fact]
    public void CompletionIsStagedAsReferenceWithStableMessageIdentity()
    {
        var outbox = new RecordingOutbox();
        var transport = MessagingTestHelpers.CreateTransport(outbox);
        var sender = GrainId.Create("sender", "one");
        var target = GrainId.Create("target", "one");
        var taskId = TaskId.Parse("root");
        var response = DurableTaskResponse.FromResult(42);

        var messageId = transport.GetCompletionMessageId(sender, new(target, taskId), taskId);
        transport.SendCompletion(messageId);
        transport.SendCompletion(transport.GetCompletionMessageId(sender, new(target, taskId), taskId));

        // The outbox journals only the reference; the runtime provides the message from the stored result.
        Assert.Empty(outbox.Messages);
        Assert.Equal([messageId, messageId], outbox.SourceMessages);
        Assert.Equal(
            DurableTaskMessageTransport.CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.CompletionSubject),
            messageId);
        var message = transport.CreateCompletionMessage(messageId, sender, new(target, taskId), taskId, response);
        Assert.Equal(target, message.ReceiverId);
        var envelope = message.Envelope;
        Assert.Equal(messageId, envelope.MessageId);
        Assert.Equal(DurableTaskMessageTypes.CompletionSubject, envelope.Subject);
        Assert.Null(envelope.DeliverAt);
        var body = MessagingTestHelpers.MessageTypes.Completion.Decode(envelope);
        Assert.Equal(42, body.Response.GetResult<int>());
        Assert.Equal(sender, body.Sender);
        Assert.Equal(taskId, body.TaskId);
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
    public void SiloRegistrationProvidesJsonJournalMetadataForTaskState()
    {
        var silo = new TestSiloBuilder();
        silo.AddDurableTasks();
        using var provider = silo.Services.BuildServiceProvider();

        // The resolver chain has no reflection fallback, so every journaled key and value type needs registered metadata.
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<JsonJournalOptions>>().Value.SerializerOptions;
        Assert.NotNull(options.GetTypeInfo(typeof(DurableTaskState)));
        Assert.NotNull(options.GetTypeInfo(typeof(DateTimeOffset)));
        var taskId = TaskId.CreateRoot("root/with\\escapes").Child("child");
        var json = JsonSerializer.Serialize(taskId, (JsonTypeInfo<TaskId>)options.GetTypeInfo(typeof(TaskId)));
        Assert.Equal(JsonSerializer.Serialize(taskId.ToString()), json);
        Assert.Equal(taskId, JsonSerializer.Deserialize(json, (JsonTypeInfo<TaskId>)options.GetTypeInfo(typeof(TaskId))));
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
    public async Task PollAsync_DeletedTask_ThrowsTypedNotFound()
    {
        var taskId = TaskId.Parse("root/expired-client-poll");
        var grain = new NotFoundDurableTaskServer(taskId);
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
    public async Task WaitAsync_DeletedTask_ThrowsTypedNotFoundWithoutRepolling()
    {
        var taskId = TaskId.Parse("root/expired-client-wait");
        var grain = new NotFoundDurableTaskServer(taskId);
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
        var grain = new NotFoundDurableTaskServer(taskId);
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
        var grain = new NotFoundDurableTaskServer(
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
