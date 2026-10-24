using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace UnitTests.ClusterServices;

public sealed partial class ControlledGrainDirectoryProtocolTests
{
    [Theory]
    [InlineData("RegisterAsync", 3)]
    [InlineData("LookupAsync", 2)]
    [InlineData("DeregisterAsync", 2)]
    public void DirectoryRequestsPreserveLegacyWireContract(string method, int cancellationSlot)
    {
        var requests = typeof(DistributedGrainDirectory).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("Invokable_IGrainDirectoryPartition_", StringComparison.Ordinal) && typeof(IInvokable).IsAssignableFrom(t))
            .Select(t => (IInvokable)Activator.CreateInstance(t)!).ToArray();
        try
        {
            var request = Assert.Single(requests, r => r.GetMethodName() == method);
            var parameters = request.GetMethod().GetParameters();
            Assert.Equal(method, request.GetMethod().GetCustomAttribute<AliasAttribute>()!.Alias);
            Assert.Equal(cancellationSlot + 1, request.GetArgumentCount());
            Assert.Equal(typeof(CancellationToken), parameters[cancellationSlot].ParameterType);
            Assert.True(parameters[cancellationSlot].HasDefaultValue);
            Assert.True(request.IsCancellable);
            var key = GrainId.Create("directory-wire", "key");
            var address = AdmissionAddress(key, SiloAddress.FromParsableString("127.0.0.1:11111@101"));
            request.SetArgument(0, new MembershipVersion(42));
            request.SetArgument(1, method == "LookupAsync" ? key : address);
            if (cancellationSlot == 3)
            {
                request.SetArgument(2, address);
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            request.SetArgument(cancellationSlot, cancellation.Token);
            using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var serializer = services.GetRequiredService<Serializer>();
            var bytes = serializer.SerializeToArray(request);
            using var copy = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize<IInvokable>(bytes));
            Assert.Equal(new MembershipVersion(42), copy.GetArgument(0));
            Assert.False(copy.GetCancellationToken().IsCancellationRequested);
            using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
            var reader = Reader.Create(bytes, session);
            Assert.Equal(WireType.TagDelimited, reader.ReadFieldHeader().WireType);
            var fields = new List<uint>();
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndObject)
                {
                    break;
                }
                if (header.IsEndBaseFields)
                {
                    fields.Clear();
                    fieldId = 0;
                    continue;
                }

                fields.Add(fieldId += header.FieldIdDelta);
                reader.ConsumeUnknownField(header);
            }

            Assert.Equal(Enumerable.Range(0, cancellationSlot).Select(i => (uint)i), fields);
        }
        finally
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("RegisterAsync", 0)]
    [InlineData("LookupAsync", 0)]
    [InlineData("DeregisterAsync", 0)]
    [InlineData("RegisterAsync", 1)]
    [InlineData("LookupAsync", 1)]
    [InlineData("DeregisterAsync", 1)]
    [InlineData("RegisterAsync", 2)]
    [InlineData("LookupAsync", 2)]
    [InlineData("DeregisterAsync", 2)]
    [InlineData("RegisterAsync", -2)]
    [InlineData("LookupAsync", -2)]
    [InlineData("DeregisterAsync", -2)]
    public async Task DirectoryAdmissionRequiresRequestedViewOrNewer(string operation, int lag)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture();
        await StartAdmissionFixture(fixture, token);
        var owner = fixture.Nodes[0];
        var caller = fixture.Nodes[1];
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "minimum-view");
        var address = AdmissionAddress(key, caller.Address);
        if (operation != "RegisterAsync")
        {
            address = (await RegisterRemotelyAsync(fixture, caller, owner, address, null, new(1), "seed", token))!;
        }

        var receiverVersion = lag == 0 || lag < 0 ? 3 : lag == 1 ? 2 : 1;
        var requestVersion = lag < 0 ? 1 : 3;
        if (receiverVersion > 1)
        {
            await PublishAdmissionView(fixture, receiverVersion, [owner], fixture.Nodes, token);
        }

        if (requestVersion > 1)
        {
            await PublishAdmissionView(fixture, requestVersion, [caller], fixture.Nodes, token);
        }

        var refresh = owner.Membership.WaitForRefreshAsync(token);
        var invocation = InvokeDirectoryOperation(operation, caller.Directory, address, token);
        var envelope = await AdmissionEnvelope(fixture, operation, key, requestVersion, token);
        var delivery = fixture.Transport.DeliverAsync(envelope.Sequence);
        var mustRefresh = lag > 0;
        if (mustRefresh)
        {
            Assert.Equal(new MembershipVersion(requestVersion), await fixture.GuardAsync(refresh, "required view", token));
            Assert.False(invocation.IsCompleted);
            await PublishAdmissionView(fixture, requestVersion, [owner], fixture.Nodes, token);
        }

        await fixture.GuardAsync(delivery, "delivery", token);
        await fixture.GuardAsync(invocation, "completion", token);
        Assert.Equal(mustRefresh, refresh.IsCompletedSuccessfully);
        Assert.Equal(new MembershipVersion(mustRefresh ? requestVersion : receiverVersion), owner.Partition.CurrentView.Version);
        Assert.Equal(1, envelope.InvocationCount);
        var stored = await owner.ProbeLocalAsync(key, token);
        if (operation == "DeregisterAsync")
        {
            Assert.Null(stored);
        }
        else
        {
            var result = await (Task<GrainAddress?>)invocation;
            AssertAddress(result!, stored);
            Assert.Equal(address.ActivationId, result!.ActivationId);
            Assert.Equal(address.SiloAddress, result.SiloAddress);
            Assert.Equal(key, result.GrainId);
            Assert.Equal(new MembershipVersion(operation == "RegisterAsync" ? owner.Partition.CurrentView.Version.Value : 1), result.MembershipVersion);
        }
    }

    [Fact]
    public async Task RecoveryAdvancementRequiresStrictRetryBeforeCompletion()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture();
        await StartAdmissionFixture(fixture, token);
        var owner = fixture.Nodes[0];
        var caller = fixture.Nodes[1];
        var address = AdmissionAddress(FindGrainIdOwnedBy(fixture, owner.Address, "recovery-floor"), caller.Address);
        var invocation = caller.Directory.Register(address, token);
        var first = await AdmissionEnvelope(fixture, "RegisterAsync", address.GrainId, 1, token);
        await fixture.GuardAsync(fixture.Transport.CaptureResponseAsync(first.Sequence), "capture older execution", token);
        Assert.False(invocation.IsCompleted);

        await PublishAdmissionView(fixture, 2, [caller], fixture.Nodes, token);
        await caller.Directory.RunOrQueueTask(async () =>
        {
            await caller.Directory.GetRegisteredActivations(new(2), RingRange.Full, false, token);
        });
        var refresh = owner.Membership.WaitForRefreshAsync(token);
        fixture.Transport.ReleaseCapturedResponse(first.Sequence);
        var retry = await AdmissionEnvelope(fixture, "RegisterAsync", address.GrainId, 2, token);
        var delivery = fixture.Transport.DeliverAsync(retry.Sequence);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(refresh, "recovery refresh", token));
        Assert.False(invocation.IsCompleted);
        await PublishAdmissionView(fixture, 2, [owner], fixture.Nodes, token);
        await fixture.GuardAsync(delivery, "retry delivery", token);
        Assert.Equal(address.ActivationId, (await fixture.GuardAsync(invocation, "retry completion", token))!.ActivationId);
        Assert.Equal(2, fixture.Transport.Envelopes.Count(e => e.Operation == "RegisterAsync"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryAdmissionWaitsForAcquisitionAndCancellationPreservesSharedProgress(bool loseOwnershipWhileWaiting)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture(TimeSpan.Zero,
            (SiloAddress.FromParsableString("127.0.0.1:11111@101"), 0x4000_0000u),
            (SiloAddress.FromParsableString("127.0.0.1:11112@102"), 0x4100_0000u));
        var receiver = fixture.Nodes[0];
        var source = fixture.Nodes[1];
        await fixture.StartAsync(token);
        await fixture.PublishAndObserveAsync(0, [], false, token);
        var acquired = fixture.WaitForRangeOperationAsync(source, 1, RingRange.Full, GrainDirectoryEvents.AcquireOperationName, false, "initial acquire", token);
        await fixture.PublishAndObserveAsync(1, [source], false, token);
        await fixture.GuardAsync(acquired, "initial acquire", token);
        var key = FindGrainIdOwnedBy(fixture, receiver.Address, "acquisition");
        var address = AdmissionAddress(key, source.Address);
        var expected = await RegisterRemotelyAsync(fixture, receiver, source, address, null, new(1), "seed", token);
        await fixture.PublishAndObserveAsync(2, fixture.Nodes, false, token);
        var snapshot = await fixture.Transport.WaitForQueuedAsync(e => e.Operation == "GetSnapshotAsync", "snapshot", token);
        await fixture.GuardAsync(fixture.Transport.CaptureResponseAsync(snapshot.Sequence), "snapshot capture", token);
        await PublishAdmissionView(fixture, 3, [source], fixture.Nodes, token);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = DirectLookup(receiver, new(2), key, cancellation.Token, firstEntered);
        Assert.False(await fixture.GuardAsync(firstEntered.Task, "first waiter entered", token));
        var surviving = DirectLookup(receiver, new(2), key, token, secondEntered);
        Assert.False(await fixture.GuardAsync(secondEntered.Task, "second waiter entered", token));
        Assert.False(canceled.IsCompleted);
        Assert.False(surviving.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(surviving.IsCompleted);
        if (loseOwnershipWhileWaiting)
        {
            await fixture.PublishAndObserveAsync(4, [receiver], [source], [], false, token, shuttingDownNodes: [receiver]);
        }

        fixture.Transport.ReleaseCapturedResponse(snapshot.Sequence);
        var acknowledgement = await fixture.Transport.WaitForQueuedAsync(e => e.Operation == "AcknowledgeSnapshotTransferAsync", "acknowledgement", token);
        await fixture.GuardAsync(fixture.Transport.DeliverAsync(acknowledgement.Sequence), "acknowledgement delivery", token);
        var result = await fixture.GuardAsync(surviving, "acquired lookup", token);
        Assert.Equal(!loseOwnershipWhileWaiting, result.TryGetResult(new(2), out var actual));
        if (loseOwnershipWhileWaiting)
        {
            Assert.Equal(new MembershipVersion(4), result.Version);
            Assert.Null(actual);
        }
        else
        {
            AssertAddress(expected!, actual);
            Assert.Equal(new MembershipVersion(2), receiver.Partition.CurrentView.Version);
        }
    }

    [Theory]
    [InlineData("RegisterAsync")]
    [InlineData("LookupAsync")]
    [InlineData("DeregisterAsync")]
    public async Task DirectoryLivenessUsesInstalledMembershipBeforeDeathLease(string operation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = CreateAdmissionFixture();
        await StartAdmissionFixture(fixture, token);
        var caller = fixture.Nodes[0];
        var owner = fixture.Nodes[1];
        var host = fixture.Nodes[2];
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "projected-death");
        var original = (await RegisterRemotelyAsync(fixture, caller, owner, AdmissionAddress(key, host.Address), null, new(1), "seed", token))!;
        var proposed = AdmissionAddress(key, caller.Address);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspection = owner.Partition.RunOrQueueTask(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15), token));
            var partition = (IGrainDirectoryPartition)owner.Partition;
            if (operation == "DeregisterAsync")
            {
                var response = partition.DeregisterAsync(new(1), proposed, token);
                Assert.True(response.IsCompletedSuccessfully);
                Assert.True(response.Result.TryGetResult(new(1), out var removed));
                Assert.False(removed);
            }
            else
            {
                var response = operation == "LookupAsync"
                    ? partition.LookupAsync(new(1), key, token)
                    : Register();
                Assert.True(response.IsCompletedSuccessfully);
                Assert.True(response.Result.TryGetResult(new(1), out var result));
                AssertAddress(original, result);
            }

            var retained = partition.LookupAsync(new(1), key, token);
            Assert.True(retained.IsCompletedSuccessfully);
            Assert.True(retained.Result.TryGetResult(new(1), out var retainedAddress));
            AssertAddress(original, retainedAddress);
            return Task.CompletedTask;

            async ValueTask<DirectoryResult<GrainAddress?>> Register()
            {
                var response = await partition.RegisterAsync(new(1), proposed, null, token);
                Assert.True(response.TryGetResult(new(1), out var value));
                return DirectoryResult.FromResult<GrainAddress?>(value, response.Version);
            }
        });
        try
        {
            await fixture.GuardAsync(entered.Task, "hold partition turn", token);
            var snapshot = HostSnapshot(fixture, host, SiloStatus.Dead);
            await owner.Membership.PublishAsync(snapshot);
            await fixture.GuardAsync(owner.Directory.RefreshViewAsync(new(2), token).AsTask(), "publish directory projection", token);
            Assert.Equal(new MembershipVersion(1), owner.Partition.CurrentView.Version);
        }
        finally
        {
            release.Set();
        }

        await fixture.GuardAsync(inspection, "admitted-view liveness", token);
    }

    private static async Task StartAdmissionFixture(ControlledProtocolFixture fixture, CancellationToken token)
    {
        await fixture.StartAsync(token);
        await fixture.PublishAndObserveAsync(0, [], false, token);
        await fixture.PublishAndObserveAsync(1, fixture.Nodes, true, token);
    }

    [Theory]
    [InlineData("RegisterAsync", SiloStatus.Active)]
    [InlineData("DeregisterAsync", SiloStatus.Active)]
    [InlineData("RegisterAsync", SiloStatus.Dead)]
    [InlineData("DeregisterAsync", SiloStatus.Dead)]
    public async Task MutationHostChangeRefreshesLaggingCallerProjection(string operation, SiloStatus status)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = CreateAdmissionFixture();
        var caller = fixture.Nodes[0];
        var owner = fixture.Nodes[1];
        var host = fixture.Nodes[2];
        await fixture.StartAsync(token);
        await fixture.PublishAndObserveAsync(0, [], false, token);
        var oldMembers = status == SiloStatus.Active ? new[] { caller, owner } : fixture.Nodes.ToArray();
        await fixture.PublishAndObserveAsync(1, oldMembers, oldMembers, [], true, token);
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "host-change");
        var address = AdmissionAddress(key, host.Address);
        if (status == SiloStatus.Dead)
        {
            address = (await RegisterRemotelyAsync(fixture, caller, owner, address, null, new(1), "seed", token))!;
        }

        var before = fixture.Transport.Envelopes.Count;
        caller.Membership.HoldPublication(HostSnapshot(fixture, host, status));
        var callerRefresh = caller.Membership.WaitForRefreshAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var invocation = InvokeDirectoryOperation(operation, caller.Directory, address, cancellation.Token);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(callerRefresh, "caller host refresh", token));
        Assert.Equal(before, fixture.Transport.Envelopes.Count);
        Assert.False(invocation.IsCompleted);

        caller.Membership.ReleasePublication();
        var envelope = await AdmissionEnvelope(fixture, operation, key, 2, token);
        var receiverRefresh = owner.Membership.WaitForRefreshAsync(token);
        var delivery = fixture.Transport.DeliverAsync(envelope.Sequence);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(receiverRefresh, "receiver host refresh", token));
        Assert.False(invocation.IsCompleted);
        var active = status == SiloStatus.Active ? fixture.Nodes.ToArray() : [caller, owner];
        await fixture.PublishAndObserveAsync(2, [owner], active, status == SiloStatus.Dead ? [host] : [], false, token);
        await fixture.GuardAsync(delivery, "host-aware delivery", token);

        if (operation == "RegisterAsync" && status == SiloStatus.Dead)
        {
            var response = Assert.IsType<DirectoryResult<GrainAddress>>(envelope.Response);
            Assert.True(response.RetryAfterDelay > TimeSpan.Zero);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        }
        else
        {
            await fixture.GuardAsync(invocation, "host-aware completion", token);
            if (operation == "RegisterAsync")
            {
                var registration = await (Task<GrainAddress?>)invocation;
                Assert.Equal(new MembershipVersion(2), registration!.MembershipVersion);
                Assert.Equal(address.ActivationId, registration.ActivationId);
            }
            else
            {
                Assert.True(Assert.IsType<DirectoryResult<bool>>(envelope.Response).TryGetResult(new(2), out var removed));
                Assert.False(removed);
            }
        }

        Assert.Single(fixture.Transport.Envelopes.Skip(before), e => e.Operation == operation && e.GrainId == key);
    }

    [Fact]
    public async Task NewlyAssignedPartitionRefreshesBeforeServing()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture(TimeSpan.Zero,
            (SiloAddress.FromParsableString("127.0.0.1:11111@101"), 0x4000_0000u),
            (SiloAddress.FromParsableString("127.0.0.1:11112@102"), 0x4100_0000u));
        var receiver = fixture.Nodes[0];
        var source = fixture.Nodes[1];
        await fixture.StartAsync(token);
        await fixture.PublishAndObserveAsync(0, [], false, token);
        var initial = fixture.WaitForRangeOperationAsync(source, 1, RingRange.Full, GrainDirectoryEvents.AcquireOperationName, false, "initial", token);
        await fixture.PublishAndObserveAsync(1, [source], false, token);
        await fixture.GuardAsync(initial, "initial ready", token);
        await PublishAdmissionView(fixture, 2, [source], fixture.Nodes, token);
        var address = AdmissionAddress(FindGrainIdOwnedBy(fixture, receiver.Address, "new-owner"), source.Address);
        var refresh = receiver.Membership.WaitForRefreshAsync(token);
        var invocation = source.Directory.Register(address, token);
        var envelope = await AdmissionEnvelope(fixture, "RegisterAsync", address.GrainId, 2, token);
        var delivery = fixture.Transport.DeliverAsync(envelope.Sequence);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(refresh, "new owner refresh", token));
        Assert.False(invocation.IsCompleted);
        await PublishAdmissionView(fixture, 2, [receiver], fixture.Nodes, token);
        var snapshot = await fixture.Transport.WaitForQueuedAsync(e => e.Operation == "GetSnapshotAsync", "new owner snapshot", token);
        Assert.False(invocation.IsCompleted);
        await fixture.GuardAsync(fixture.Transport.DeliverAsync(snapshot.Sequence), "install snapshot", token);
        var ack = await fixture.Transport.WaitForQueuedAsync(e => e.Operation == "AcknowledgeSnapshotTransferAsync", "new owner ack", token);
        await fixture.GuardAsync(fixture.Transport.DeliverAsync(ack.Sequence), "ack", token);
        await fixture.GuardAsync(delivery, "registration delivery", token);
        var result = await fixture.GuardAsync(invocation, "new owner registration", token);
        Assert.Equal(address.ActivationId, result!.ActivationId);
        Assert.Equal(new MembershipVersion(2), result.MembershipVersion);
    }

    private static Task PublishAdmissionView(ControlledProtocolFixture fixture, long version, IReadOnlyCollection<ControlledNode> observers,
        IReadOnlyCollection<ControlledNode> active, CancellationToken token) =>
        fixture.PublishAndObserveAsync(version, observers, active, [], false, token);

    private static GrainAddress AdmissionAddress(GrainId key, SiloAddress host) =>
        new() { GrainId = key, SiloAddress = host, ActivationId = ActivationId.NewId(), MembershipVersion = MembershipVersion.MinValue };

    private static Task InvokeDirectoryOperation(string operation, DistributedGrainDirectory directory, GrainAddress address, CancellationToken token) =>
        operation switch
        {
            "RegisterAsync" => directory.Register(address, token),
            "LookupAsync" => directory.Lookup(address.GrainId, token),
            "DeregisterAsync" => directory.Unregister(address, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task<RpcEnvelope> AdmissionEnvelope(ControlledProtocolFixture fixture, string operation, GrainId key, long version, CancellationToken token) =>
        fixture.Transport.WaitForQueuedAsync(e => e.Operation == operation && e.GrainId == key && e.MembershipVersion == new MembershipVersion(version), operation, token);

    private static async Task<DirectoryResult<GrainAddress?>> DirectLookup(
        ControlledNode node, MembershipVersion version, GrainId key, CancellationToken token, TaskCompletionSource<bool> entered)
    {
        DirectoryResult<GrainAddress?> result = default;
        await node.Partition.RunOrQueueTask(async () =>
        {
            var invocation = ((IGrainDirectoryPartition)node.Partition).LookupAsync(version, key, token);
            entered.SetResult(invocation.IsCompleted);
            result = await invocation;
        });
        return result;
    }

    private static ControlledProtocolFixture CreateAdmissionFixture() => new(TimeSpan.FromMinutes(5),
        (SiloAddress.FromParsableString("127.0.0.1:24111@201"), 0x1000_0000u),
        (SiloAddress.FromParsableString("127.0.0.1:24112@202"), 0x4000_0000u),
        (SiloAddress.FromParsableString("127.0.0.1:24113@203"), 0x8000_0000u));

    private static ClusterMembershipSnapshot HostSnapshot(ControlledProtocolFixture fixture, ControlledNode host, SiloStatus status) =>
        new(fixture.Nodes.ToImmutableDictionary(n => n.Address,
            n => new ClusterMember(n.Address, n == host ? status : SiloStatus.Active, $"controlled-{n.Address.Endpoint.Port}", n == host && status == SiloStatus.Dead)), new(2));
}
