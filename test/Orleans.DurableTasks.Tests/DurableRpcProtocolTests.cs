using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
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

        Orleans.DurableMessaging.HierarchicalKey? correlationKey = taskId.ToHierarchicalKey();

        Assert.NotNull(correlationKey);
        Assert.Equal(taskId, correlationKey.ToTaskId());
        Assert.Equal(TaskId.None, ((Orleans.DurableMessaging.HierarchicalKey?)null).ToTaskId());
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
