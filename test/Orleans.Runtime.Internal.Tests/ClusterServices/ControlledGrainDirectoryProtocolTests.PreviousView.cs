using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
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
    [InlineData("RegisterAsync", 4)]
    [InlineData("LookupAsync", 3)]
    [InlineData("DeregisterAsync", 3)]
    public void PreviousViewPermissionPreservesGeneratedArgumentSlots(string method, int permissionSlot)
    {
        Assert.True(new GrainDirectoryOptions().EnablePreviousViewRequests);
        var requests = typeof(DistributedGrainDirectory).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("Invokable_IGrainDirectoryPartition_", StringComparison.Ordinal) && typeof(IInvokable).IsAssignableFrom(t))
            .Select(t => (IInvokable)Activator.CreateInstance(t)!).ToArray();
        try
        {
            var request = Assert.Single(requests, r => r.GetMethodName() == method);
            var parameters = request.GetMethod().GetParameters();
            Assert.Equal(method, request.GetMethod().GetCustomAttribute<AliasAttribute>()!.Alias);
            Assert.Equal(permissionSlot + 1, request.GetArgumentCount());
            Assert.Equal(typeof(CancellationToken), parameters[permissionSlot - 1].ParameterType);
            Assert.Equal(typeof(bool), parameters[permissionSlot].ParameterType);
            Assert.Equal(false, parameters[permissionSlot].DefaultValue);
            Assert.True(request.IsCancellable);
            var key = GrainId.Create("previous-view-wire", "key");
            var address = PreviousViewAddress(key, SiloAddress.FromParsableString("127.0.0.1:11111@101"));
            request.SetArgument(0, new MembershipVersion(42));
            request.SetArgument(1, method == "LookupAsync" ? key : address);
            if (permissionSlot == 4)
            {
                request.SetArgument(2, address);
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            request.SetArgument(permissionSlot - 1, cancellation.Token);
            request.SetArgument(permissionSlot, true);
            Assert.Equal(true, request.GetArgument(permissionSlot));
            using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var serializer = services.GetRequiredService<Serializer>();
            var bytes = serializer.SerializeToArray(request);
            using var copy = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize<IInvokable>(bytes));
            Assert.Equal(new MembershipVersion(42), copy.GetArgument(0));
            Assert.Equal(true, copy.GetArgument(permissionSlot));
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

            Assert.Equal(Enumerable.Range(0, permissionSlot - 1).Append(permissionSlot).Select(i => (uint)i), fields);
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
    [InlineData("RegisterAsync", true, 1)]
    [InlineData("LookupAsync", true, 1)]
    [InlineData("DeregisterAsync", true, 1)]
    [InlineData("RegisterAsync", false, 1)]
    [InlineData("LookupAsync", false, 1)]
    [InlineData("DeregisterAsync", false, 1)]
    [InlineData("RegisterAsync", true, 2)]
    [InlineData("LookupAsync", true, 2)]
    [InlineData("DeregisterAsync", true, 2)]
    [InlineData("RegisterAsync", true, -2)]
    [InlineData("LookupAsync", true, -2)]
    [InlineData("DeregisterAsync", true, -2)]
    public async Task PreviousViewAdmissionUsesOnlyAnImmediateOwnedPredecessor(string operation, bool enabled, int lag)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture(enabled);
        await StartPreviousViewFixture(fixture, token);
        var owner = fixture.Nodes[0];
        var caller = fixture.Nodes[1];
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "previous-view");
        var address = PreviousViewAddress(key, caller.Address);
        if (operation != "RegisterAsync")
        {
            address = (await RegisterRemotelyAsync(fixture, caller, owner, address, null, new(1), "seed", token))!;
        }

        // In the fast case the caller skips v2 entirely, while the receiver installs it.
        var receiverVersion = lag == 1 ? 2 : lag < 0 ? 3 : 1;
        var requestVersion = lag < 0 ? 1 : 3;
        if (receiverVersion > 1)
        {
            await PublishPreviousView(fixture, receiverVersion, [owner], fixture.Nodes, token);
        }

        if (requestVersion > 1)
        {
            await PublishPreviousView(fixture, requestVersion, [caller], fixture.Nodes, token);
        }

        var refresh = owner.Membership.WaitForRefreshAsync(token);
        var invocation = InvokePreviousViewOperation(operation, caller.Directory, address, token);
        var envelope = await PreviousViewEnvelope(fixture, operation, key, requestVersion, token);
        var delivery = fixture.Transport.DeliverAsync(envelope.Sequence);
        var mustRefresh = lag > 0 && (!enabled || lag > 1);
        if (mustRefresh)
        {
            Assert.Equal(new MembershipVersion(requestVersion), await fixture.GuardAsync(refresh, "required view", token));
            Assert.False(invocation.IsCompleted);
            await PublishPreviousView(fixture, requestVersion, [owner], fixture.Nodes, token);
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
        await using var fixture = new ControlledProtocolFixture(true);
        await StartPreviousViewFixture(fixture, token);
        var owner = fixture.Nodes[0];
        var caller = fixture.Nodes[1];
        var address = PreviousViewAddress(FindGrainIdOwnedBy(fixture, owner.Address, "recovery-floor"), caller.Address);
        await PublishPreviousView(fixture, 2, [caller], fixture.Nodes, token);
        var invocation = caller.Directory.Register(address, token);
        var first = await PreviousViewEnvelope(fixture, "RegisterAsync", address.GrainId, 2, token);
        await fixture.GuardAsync(fixture.Transport.CaptureResponseAsync(first.Sequence), "capture older execution", token);
        Assert.False(invocation.IsCompleted);

        await caller.Directory.RunOrQueueTask(async () =>
        {
            await caller.Directory.GetRegisteredActivations(new(2), RingRange.Full, false, token);
        });
        var refresh = owner.Membership.WaitForRefreshAsync(token);
        fixture.Transport.ReleaseCapturedResponse(first.Sequence);
        var retry = await PreviousViewEnvelope(fixture, "RegisterAsync", address.GrainId, 2, token);
        var delivery = fixture.Transport.DeliverAsync(retry.Sequence);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(refresh, "recovery refresh", token));
        Assert.False(invocation.IsCompleted);
        await PublishPreviousView(fixture, 2, [owner], fixture.Nodes, token);
        await fixture.GuardAsync(delivery, "retry delivery", token);
        Assert.Equal(address.ActivationId, (await fixture.GuardAsync(invocation, "retry completion", token))!.ActivationId);
        Assert.Equal(2, fixture.Transport.Envelopes.Count(e => e.Operation == "RegisterAsync"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviousViewWaitsForAcquisitionAndCancellationPreservesSharedProgress(bool loseOwnershipWhileWaiting)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture(true, TimeSpan.Zero,
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
        var address = PreviousViewAddress(key, source.Address);
        var expected = await RegisterRemotelyAsync(fixture, receiver, source, address, null, new(1), "seed", token);
        await fixture.PublishAndObserveAsync(2, fixture.Nodes, false, token);
        var snapshot = await fixture.Transport.WaitForQueuedAsync(e => e.Operation == "GetSnapshotAsync", "snapshot", token);
        await fixture.GuardAsync(fixture.Transport.CaptureResponseAsync(snapshot.Sequence), "snapshot capture", token);
        await PublishPreviousView(fixture, 3, [source], fixture.Nodes, token);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstEntered = fixture.WaitForDirectoryEventAsync(nameof(GrainDirectoryEvents.PreviousViewAdmission),
            e => e.Payload is GrainDirectoryEvents.PreviousViewAdmission p && p.SiloAddress.Equals(receiver.Address)
                && p.GrainId == key && p.Reason == "range-gate", "first range gate", token);
        var canceled = DirectPreviousLookup(receiver, new(3), key, cancellation.Token);
        var firstEvent = await fixture.GuardAsync(firstEntered, "first waiter entered", token);
        var secondEntered = fixture.WaitForDirectoryEventAsync(nameof(GrainDirectoryEvents.PreviousViewAdmission),
            e => e.Payload is GrainDirectoryEvents.PreviousViewAdmission p && p.SiloAddress.Equals(receiver.Address)
                && p.GrainId == key && p.Reason == "range-gate" && !ReferenceEquals(e.Payload, firstEvent.Payload), "second range gate", token);
        var surviving = DirectPreviousLookup(receiver, new(3), key, token);
        await fixture.GuardAsync(secondEntered, "second waiter entered", token);
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
        Assert.Equal(!loseOwnershipWhileWaiting, result.TryGetResult(new(3), out var actual));
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
    public async Task PreviousViewLivenessUsesInstalledMembershipBeforeDeathLease(string operation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = PreviousViewThreeNodes();
        await StartPreviousViewFixture(fixture, token);
        var caller = fixture.Nodes[0];
        var owner = fixture.Nodes[1];
        var host = fixture.Nodes[2];
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "projected-death");
        var original = (await RegisterRemotelyAsync(fixture, caller, owner, PreviousViewAddress(key, host.Address), null, new(1), "seed", token))!;
        var proposed = PreviousViewAddress(key, caller.Address);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspection = owner.Partition.RunOrQueueTask(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15), token));
            var partition = (IGrainDirectoryPartition)owner.Partition;
            if (operation == "DeregisterAsync")
            {
                var response = partition.DeregisterAsync(new(2), proposed, token, true);
                Assert.True(response.IsCompletedSuccessfully);
                Assert.True(response.Result.TryGetResult(new(2), out var removed));
                Assert.False(removed);
            }
            else
            {
                var response = operation == "LookupAsync"
                    ? partition.LookupAsync(new(2), key, token, true)
                    : Register();
                Assert.True(response.IsCompletedSuccessfully);
                Assert.True(response.Result.TryGetResult(new(2), out var result));
                AssertAddress(original, result);
            }

            var retained = partition.LookupAsync(new(2), key, token, true);
            Assert.True(retained.IsCompletedSuccessfully);
            Assert.True(retained.Result.TryGetResult(new(2), out var retainedAddress));
            AssertAddress(original, retainedAddress);
            return Task.CompletedTask;

            async ValueTask<DirectoryResult<GrainAddress?>> Register()
            {
                var response = await partition.RegisterAsync(new(2), proposed, null, token, true);
                Assert.True(response.TryGetResult(new(2), out var value));
                return DirectoryResult.FromResult<GrainAddress?>(value, response.Version);
            }
        });
        try
        {
            await fixture.GuardAsync(entered.Task, "hold partition turn", token);
            var snapshot = PreviousViewHostSnapshot(fixture, host, SiloStatus.Dead);
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

    private static async Task StartPreviousViewFixture(ControlledProtocolFixture fixture, CancellationToken token)
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
        await using var fixture = PreviousViewThreeNodes();
        var caller = fixture.Nodes[0];
        var owner = fixture.Nodes[1];
        var host = fixture.Nodes[2];
        await fixture.StartAsync(token);
        await fixture.PublishAndObserveAsync(0, [], false, token);
        var oldMembers = status == SiloStatus.Active ? new[] { caller, owner } : fixture.Nodes.ToArray();
        await fixture.PublishAndObserveAsync(1, oldMembers, oldMembers, [], true, token);
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "host-change");
        var address = PreviousViewAddress(key, host.Address);
        if (status == SiloStatus.Dead)
        {
            address = (await RegisterRemotelyAsync(fixture, caller, owner, address, null, new(1), "seed", token))!;
        }

        var before = fixture.Transport.Envelopes.Count;
        caller.Membership.HoldPublication(PreviousViewHostSnapshot(fixture, host, status));
        var callerRefresh = caller.Membership.WaitForRefreshAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var invocation = InvokePreviousViewOperation(operation, caller.Directory, address, cancellation.Token);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(callerRefresh, "caller host refresh", token));
        Assert.Equal(before, fixture.Transport.Envelopes.Count);
        Assert.False(invocation.IsCompleted);

        caller.Membership.ReleasePublication();
        var envelope = await PreviousViewEnvelope(fixture, operation, key, 2, token);
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

    [Theory]
    [InlineData("LookupAsync")]
    [InlineData("RegisterAsync")]
    public async Task ReturnedDeadActivationRequiresStrictRetry(string operation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = PreviousViewThreeNodes();
        await StartPreviousViewFixture(fixture, token);
        var caller = fixture.Nodes[0];
        var owner = fixture.Nodes[1];
        var host = fixture.Nodes[2];
        var key = FindGrainIdOwnedBy(fixture, owner.Address, "returned-dead-host");
        var original = (await RegisterRemotelyAsync(fixture, caller, owner, PreviousViewAddress(key, host.Address), null, new(1), "seed", token))!;
        await fixture.PublishAndObserveAsync(2, [caller], [caller, owner], [host], false, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var invocation = InvokePreviousViewOperation(operation, caller.Directory, PreviousViewAddress(key, caller.Address), cancellation.Token);
        var refresh = owner.Membership.WaitForRefreshAsync(token);
        var first = await PreviousViewEnvelope(fixture, operation, key, 2, token);
        await fixture.GuardAsync(fixture.Transport.DeliverAsync(first.Sequence), "older response", token);
        Assert.False(refresh.IsCompleted);
        Assert.True(Assert.IsType<DirectoryResult<GrainAddress?>>(first.Response).TryGetResult(new(2), out var stale));
        AssertAddress(original, stale);
        var retry = await PreviousViewEnvelope(fixture, operation, key, 2, token);
        var delivery = fixture.Transport.DeliverAsync(retry.Sequence);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(refresh, "death-aware strict retry", token));
        Assert.False(invocation.IsCompleted);
        await fixture.PublishAndObserveAsync(2, [owner], [caller, owner], [host], false, token);
        await fixture.GuardAsync(delivery, "strict delivery", token);
        if (operation == "RegisterAsync")
        {
            Assert.True(Assert.IsType<DirectoryResult<GrainAddress>>(retry.Response).RetryAfterDelay > TimeSpan.Zero);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        }
        else
        {
            Assert.Null(await fixture.GuardAsync((Task<GrainAddress?>)invocation, "strict lookup", token));
        }
    }

    [Fact]
    public async Task NewlyAssignedPartitionRefreshesBeforeServing()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture(true, TimeSpan.Zero,
            (SiloAddress.FromParsableString("127.0.0.1:11111@101"), 0x4000_0000u),
            (SiloAddress.FromParsableString("127.0.0.1:11112@102"), 0x4100_0000u));
        var receiver = fixture.Nodes[0];
        var source = fixture.Nodes[1];
        await fixture.StartAsync(token);
        await fixture.PublishAndObserveAsync(0, [], false, token);
        var initial = fixture.WaitForRangeOperationAsync(source, 1, RingRange.Full, GrainDirectoryEvents.AcquireOperationName, false, "initial", token);
        await fixture.PublishAndObserveAsync(1, [source], false, token);
        await fixture.GuardAsync(initial, "initial ready", token);
        await PublishPreviousView(fixture, 2, [source], fixture.Nodes, token);
        var address = PreviousViewAddress(FindGrainIdOwnedBy(fixture, receiver.Address, "new-owner"), source.Address);
        var refresh = receiver.Membership.WaitForRefreshAsync(token);
        var invocation = source.Directory.Register(address, token);
        var envelope = await PreviousViewEnvelope(fixture, "RegisterAsync", address.GrainId, 2, token);
        var delivery = fixture.Transport.DeliverAsync(envelope.Sequence);
        Assert.Equal(new MembershipVersion(2), await fixture.GuardAsync(refresh, "new owner refresh", token));
        Assert.False(invocation.IsCompleted);
        await PublishPreviousView(fixture, 2, [receiver], fixture.Nodes, token);
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

    private static Task PublishPreviousView(ControlledProtocolFixture fixture, long version, IReadOnlyCollection<ControlledNode> observers,
        IReadOnlyCollection<ControlledNode> active, CancellationToken token) =>
        fixture.PublishAndObserveAsync(version, observers, active, [], false, token);

    private static GrainAddress PreviousViewAddress(GrainId key, SiloAddress host) =>
        new() { GrainId = key, SiloAddress = host, ActivationId = ActivationId.NewId(), MembershipVersion = MembershipVersion.MinValue };

    private static Task InvokePreviousViewOperation(string operation, DistributedGrainDirectory directory, GrainAddress address, CancellationToken token) =>
        operation switch
        {
            "RegisterAsync" => directory.Register(address, token),
            "LookupAsync" => directory.Lookup(address.GrainId, token),
            "DeregisterAsync" => directory.Unregister(address, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task<RpcEnvelope> PreviousViewEnvelope(ControlledProtocolFixture fixture, string operation, GrainId key, long version, CancellationToken token) =>
        fixture.Transport.WaitForQueuedAsync(e => e.Operation == operation && e.GrainId == key && e.MembershipVersion == new MembershipVersion(version), operation, token);

    private static async Task<DirectoryResult<GrainAddress?>> DirectPreviousLookup(ControlledNode node, MembershipVersion version, GrainId key, CancellationToken token)
    {
        DirectoryResult<GrainAddress?> result = default;
        await node.Partition.RunOrQueueTask(async () => result = await ((IGrainDirectoryPartition)node.Partition).LookupAsync(version, key, token, true));
        return result;
    }

    private static ControlledProtocolFixture PreviousViewThreeNodes() => new(true, TimeSpan.FromMinutes(5),
        (SiloAddress.FromParsableString("127.0.0.1:24111@201"), 0x1000_0000u),
        (SiloAddress.FromParsableString("127.0.0.1:24112@202"), 0x4000_0000u),
        (SiloAddress.FromParsableString("127.0.0.1:24113@203"), 0x8000_0000u));

    private static ClusterMembershipSnapshot PreviousViewHostSnapshot(ControlledProtocolFixture fixture, ControlledNode host, SiloStatus status) =>
        new(fixture.Nodes.ToImmutableDictionary(n => n.Address,
            n => new ClusterMember(n.Address, n == host ? status : SiloStatus.Active, $"controlled-{n.Address.Endpoint.Port}", n == host && status == SiloStatus.Dead)), new(2));
}
