using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Hosting;
using Orleans.Runtime.Scheduler;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace UnitTests.ClusterServices;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT")]
[Collection(ControlledGrainDirectoryProtocolCollection.Name)]
public sealed partial class ControlledGrainDirectoryProtocolTests
{
    [Fact]
    public async Task LifecycleStartup_PublishedMembershipRoutesProtocolOnDestinationScheduler()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = new ControlledProtocolFixture();

        Assert.All(fixture.Nodes, node =>
        {
            var target = node.Activations.FindTarget(
                GrainDirectoryPartition.CreateGrainId(node.Address, 0).GrainId);
            Assert.Same(node.Partition, Assert.IsType<GrainDirectoryPartition>(target));
            Assert.Same(fixture.TimeProvider, node.TimeProvider);
        });
        Assert.DoesNotContain(
            fixture.DirectoryEvents.GetEvents(nameof(GrainDirectoryEvents.MembershipVersionObserved)),
            diagnostic => diagnostic.Payload is GrainDirectoryEvents.MembershipVersionObserved payload
                && fixture.Nodes.Any(node => node.Address.Equals(payload.SiloAddress)));

        await fixture.StartAsync(cancellationToken);
        // Startup can publish the initial empty view before the first membership update.
        Assert.DoesNotContain(
            fixture.DirectoryEvents.GetEvents(nameof(GrainDirectoryEvents.MembershipVersionObserved)),
            diagnostic => diagnostic.Payload is GrainDirectoryEvents.MembershipVersionObserved payload
                && fixture.Nodes.Any(node => node.Address.Equals(payload.SiloAddress))
                && payload.Version != MembershipVersion.MinValue);

        // Version zero gives the lifecycle loop a contiguous empty predecessor for the
        // first active view, avoiding any synthetic/direct membership application.
        await fixture.PublishAndObserveAsync(
            version: 0,
            activeNodes: [],
            assertRangeCompletion: false,
            cancellationToken);

        const long activeVersionValue = 1;
        var activeVersion = new MembershipVersion(activeVersionValue);
        await fixture.PublishAndObserveAsync(
            activeVersionValue,
            fixture.Nodes,
            assertRangeCompletion: true,
            cancellationToken);

        var grainId = GrainId.Create("controlled-directory", "phase-1-grain");
        var expectedOwner = fixture.Ownership.GetOwner(grainId.GetUniformHashCode());
        var owner = fixture.GetNode(expectedOwner);
        var caller = Assert.Single(fixture.Nodes, node => !node.Address.Equals(expectedOwner));
        var proposed = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("16c40d61-f52b-42be-a273-127689c5de11")),
            SiloAddress = caller.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expected = fixture.Registrations.Register(
            proposed,
            previousAddress: null,
            activeVersion,
            fixture.Nodes.Select(static node => node.Address).ToHashSet());
        fixture.ExpectedRecord = expected;

        var registerTask = caller.Directory.Register(proposed, cancellationToken);
        var registerEnvelopeTask = fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(caller.Address)
                && envelope.Destination.Equals(owner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.RegisterAsync)
                && envelope.MembershipVersion == activeVersion
                && envelope.GrainId == grainId,
            "register queued",
            cancellationToken);
        var firstRegisterResult = await fixture.GuardAsync(
            Task.WhenAny(registerEnvelopeTask, registerTask),
            "register dispatch",
            cancellationToken);
        if (ReferenceEquals(firstRegisterResult, registerTask))
        {
            _ = await registerTask;
        }

        var registerEnvelope = await registerEnvelopeTask;
        Assert.False(registerTask.IsCompleted);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(registerEnvelope.Sequence),
            "deliver registration",
            cancellationToken);
        var registered = await fixture.GuardAsync(registerTask, "registration response", cancellationToken);
        fixture.ActualRecord = registered;

        AssertAddress(expected, registered);
        Assert.Equal(RpcEnvelopeState.Delivered, registerEnvelope.State);
        Assert.True(registerEnvelope.TargetRanOnDestinationScheduler);

        var lookupTask = caller.Directory.Lookup(grainId, cancellationToken);
        var lookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(caller.Address)
                && envelope.Destination.Equals(owner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == activeVersion
                && envelope.GrainId == grainId,
            "lookup queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(lookupEnvelope.Sequence),
            "deliver lookup",
            cancellationToken);
        var routedLookup = await fixture.GuardAsync(lookupTask, "lookup response", cancellationToken);

        AssertAddress(expected, routedLookup);
        Assert.True(lookupEnvelope.TargetRanOnDestinationScheduler);
        AssertAddress(expected, await owner.ProbeLocalAsync(grainId, cancellationToken));
        foreach (var nonOwner in fixture.Nodes.Where(node => !node.Address.Equals(expectedOwner)))
        {
            Assert.Null(await nonOwner.ProbeLocalAsync(grainId, cancellationToken));
        }

        var client = fixture.Transport.CreateDirectoryClientProxy(caller.Address, owner.Address);
        var recoveryTask = client.GetRegisteredActivations(
            activeVersion,
            RingRange.Full,
            isValidation: true,
            cancellationToken).AsTask();
        var recoveryEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(caller.Address)
                && envelope.Destination.Equals(owner.Address)
                && envelope.Operation == nameof(IGrainDirectoryClient.GetRegisteredActivations),
            "directory client queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(recoveryEnvelope.Sequence),
            "deliver directory client call",
            cancellationToken);
        var recovered = await fixture.GuardAsync(recoveryTask, "directory client response", cancellationToken);

        Assert.Empty(recovered.Value);
        Assert.True(recoveryEnvelope.TargetRanOnDestinationScheduler);
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
        Assert.InRange(fixture.Transport.Envelopes.Count, 3, ControlledDirectoryTransport.MaximumEnvelopeCount);
        Assert.True(
            fixture.Trace.Select(static entry => entry.Sequence).SequenceEqual(
                fixture.Trace.Select(static entry => entry.Sequence).Order()),
            fixture.DescribeState("trace ordering"));
        Assert.All(
            fixture.Trace.Select(static entry => entry.Sequence).Pairwise(),
            pair => Assert.True(pair.First < pair.Second, $"Trace sequence was not increasing: {pair}."));
        Assert.True(
            fixture.Trace[^1].Sequence <= ControlledProtocolFixture.MaximumActionCount,
            fixture.DescribeState("action cap"));
    }

    [Fact]
    public async Task CancellingBlockedRegistration_DoesNotCancelSharedTransition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var receiverAddress = SiloAddress.FromParsableString("127.0.0.1:12111@201");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:12112@202");
        var alternateAddress = SiloAddress.FromParsableString("127.0.0.1:12113@203");
        await using var fixture = CreateThreeNodeFixture(receiverAddress, sourceAddress, alternateAddress);
        var source = fixture.GetNode(sourceAddress);
        var receiver = fixture.GetNode(receiverAddress);
        var transferRange = ToRingRange(fixture.Ownership.GetRange(receiver.Address));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);

        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            version: 1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "initial single-owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, [source], assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "initial single-owner acquire completion", cancellationToken);

        var sourceRelease = fixture.WaitForRangeOperationAsync(
            source,
            version: 2,
            transferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "source release v2",
            cancellationToken);
        var receiverAcquire = fixture.WaitForRangeOperationAsync(
            receiver,
            version: 2,
            transferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "receiver acquire v2",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            version: 2,
            activeNodes: [source, receiver],
            assertRangeCompletion: false,
            cancellationToken);

        var snapshotEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == transferRange,
            "v1 snapshot request queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.CaptureResponseAsync(snapshotEnvelope.Sequence),
            "capture v1 snapshot",
            cancellationToken);
        var capturedSnapshot = Assert.IsType<GrainDirectoryPartitionSnapshot>(snapshotEnvelope.Response);
        Assert.Equal(new MembershipVersion(1), capturedSnapshot.DirectoryMembershipVersion);
        Assert.Equal(RpcEnvelopeState.ResponseCaptured, snapshotEnvelope.State);
        Assert.True(snapshotEnvelope.TargetRanOnDestinationScheduler);

        var grainId = FindGrainIdOwnedBy(fixture, receiver.Address, "cancelled-registration");
        var proposed = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("0f5efb42-bc4f-4071-8a22-64c54b174ad7")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var blockedRegistration = source.Directory.Register(proposed, callerCts.Token);
        var registrationEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(receiver.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.RegisterAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.GrainId == grainId,
            "blocked registration queued",
            cancellationToken);

        var registrationDelivery = fixture.Transport.DeliverAsync(registrationEnvelope.Sequence);
        Assert.Null(await receiver.ProbeLocalAsync(grainId, cancellationToken));
        Assert.Equal(RpcEnvelopeState.Invoking, registrationEnvelope.State);
        Assert.False(registrationDelivery.IsCompleted);
        Assert.False(blockedRegistration.IsCompleted);

        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.GuardAsync(blockedRegistration, "canceled registration caller", cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.GuardAsync(registrationDelivery, "canceled registration delivery", cancellationToken));

        Assert.Equal(RpcEnvelopeState.Faulted, registrationEnvelope.State);
        Assert.Equal(RpcEnvelopeState.ResponseCaptured, snapshotEnvelope.State);
        Assert.DoesNotContain(
            fixture.DirectoryEvents.GetEvents(nameof(GrainDirectoryEvents.RangeOperationCompleted)),
            diagnostic => diagnostic.Payload is GrainDirectoryEvents.RangeOperationCompleted payload
                && payload.SiloAddress.Equals(receiver.Address)
                && payload.PartitionIndex == 0
                && payload.Version == new MembershipVersion(2)
                && payload.Range == transferRange
                && payload.OperationName == GrainDirectoryEvents.AcquireOperationName
                && payload.Canceled);

        fixture.Transport.ReleaseCapturedResponse(snapshotEnvelope.Sequence);
        var acquireCompleted = await fixture.GuardAsync(
            receiverAcquire,
            "receiver acquire after caller cancellation",
            cancellationToken);
        var acquirePayload = Assert.IsType<GrainDirectoryEvents.RangeOperationCompleted>(acquireCompleted.Payload);
        Assert.False(acquirePayload.Canceled);
        await fixture.GuardAsync(sourceRelease, "source release v2 completion", cancellationToken);

        var acknowledgementEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(1),
            "v1 acknowledgement queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(acknowledgementEnvelope.Sequence),
            "deliver v1 acknowledgement",
            cancellationToken);

        var expected = fixture.Registrations.Register(
            proposed,
            previousAddress: null,
            new MembershipVersion(2),
            new HashSet<SiloAddress> { source.Address, receiver.Address });
        fixture.ExpectedRecord = expected;

        var successfulRegistration = source.Directory.Register(proposed, cancellationToken);
        var successfulRegistrationEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(receiver.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.RegisterAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.GrainId == grainId
                && envelope.Sequence != registrationEnvelope.Sequence,
            "second registration queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(successfulRegistrationEnvelope.Sequence),
            "deliver second registration",
            cancellationToken);
        var registered = await fixture.GuardAsync(
            successfulRegistration,
            "second registration response",
            cancellationToken);
        fixture.ActualRecord = registered;
        AssertAddress(expected, registered);

        var lookupTask = source.Directory.Lookup(grainId, cancellationToken);
        var lookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(receiver.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.GrainId == grainId,
            "post-transition lookup queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(lookupEnvelope.Sequence),
            "deliver post-transition lookup",
            cancellationToken);

        AssertAddress(expected, await fixture.GuardAsync(lookupTask, "post-transition lookup", cancellationToken));
        AssertAddress(expected, await receiver.ProbeLocalAsync(grainId, cancellationToken));
        Assert.Equal(RpcEnvelopeState.Delivered, snapshotEnvelope.State);
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    [Fact]
    public async Task DuplicateAndLateAcknowledgements_AreIdempotentAndVersionScoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstReceiverAddress = SiloAddress.FromParsableString("127.0.0.1:13111@301");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:13112@302");
        var secondReceiverAddress = SiloAddress.FromParsableString("127.0.0.1:13113@303");
        await using var fixture = CreateThreeNodeFixture(
            firstReceiverAddress,
            sourceAddress,
            secondReceiverAddress);
        var firstReceiver = fixture.GetNode(firstReceiverAddress);
        var source = fixture.GetNode(sourceAddress);
        var secondReceiver = fixture.GetNode(secondReceiverAddress);
        var firstTransferRange = ToRingRange(fixture.Ownership.GetRange(firstReceiver.Address));
        var secondTransferRange = ToRingRange(fixture.Ownership.GetRange(secondReceiver.Address));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);

        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            version: 1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "initial single-owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, [source], assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "initial single-owner acquire completion", cancellationToken);

        var grainId = FindGrainIdOwnedBy(fixture, source.Address, "ack-versioning");
        var proposed = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("822365e4-7ea7-4539-a930-3dc8af0744e7")),
            SiloAddress = firstReceiver.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expected = fixture.Registrations.Register(
            proposed,
            previousAddress: null,
            new MembershipVersion(1),
            new HashSet<SiloAddress> { source.Address });
        fixture.ExpectedRecord = expected;

        var registrationTask = firstReceiver.Directory.Register(proposed, cancellationToken);
        var registrationEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(firstReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.RegisterAsync)
                && envelope.MembershipVersion == new MembershipVersion(1)
                && envelope.GrainId == grainId,
            "initial registration queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(registrationEnvelope.Sequence),
            "deliver initial registration",
            cancellationToken);
        var registered = await fixture.GuardAsync(registrationTask, "initial registration response", cancellationToken);
        fixture.ActualRecord = registered;
        AssertAddress(expected, registered);

        var firstAcquire = fixture.WaitForRangeOperationAsync(
            firstReceiver,
            version: 2,
            firstTransferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "first receiver acquire v2",
            cancellationToken);
        var firstRelease = fixture.WaitForRangeOperationAsync(
            source,
            version: 2,
            firstTransferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "first source release v2",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            version: 2,
            activeNodes: [source, firstReceiver],
            assertRangeCompletion: false,
            cancellationToken);
        var firstSnapshotEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(firstReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == firstTransferRange,
            "first snapshot queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(firstSnapshotEnvelope.Sequence),
            "deliver first snapshot",
            cancellationToken);
        await fixture.GuardAsync(firstAcquire, "first receiver acquire completion", cancellationToken);
        await fixture.GuardAsync(firstRelease, "first source release completion", cancellationToken);
        var oldAcknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(firstReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(1),
            "old acknowledgement queued",
            cancellationToken);

        var secondAcquire = fixture.WaitForRangeOperationAsync(
            secondReceiver,
            version: 3,
            secondTransferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "second receiver acquire v3",
            cancellationToken);
        var secondRelease = fixture.WaitForRangeOperationAsync(
            source,
            version: 3,
            secondTransferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "second source release v3",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            version: 3,
            activeNodes: [source, firstReceiver, secondReceiver],
            assertRangeCompletion: false,
            cancellationToken);
        var secondSnapshotEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(secondReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Range == secondTransferRange,
            "second snapshot queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(secondSnapshotEnvelope.Sequence),
            "deliver second snapshot",
            cancellationToken);
        await fixture.GuardAsync(secondAcquire, "second receiver acquire completion", cancellationToken);
        await fixture.GuardAsync(secondRelease, "second source release completion", cancellationToken);
        var currentAcknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(secondReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(2),
            "current acknowledgement queued",
            cancellationToken);

        var oldResults = await fixture.GuardAsync(
            fixture.Transport.DeliverDuplicateAsync(oldAcknowledgement.Sequence),
            "deliver duplicate old acknowledgement",
            cancellationToken);
        Assert.True(Assert.IsType<bool>(oldResults.Original));
        Assert.True(Assert.IsType<bool>(oldResults.Duplicate));
        Assert.Equal(2, oldAcknowledgement.InvocationCount);
        Assert.Equal(1, oldAcknowledgement.CallerCompletionCount);
        Assert.Equal(RpcEnvelopeState.Duplicated, oldAcknowledgement.State);

        var probe = fixture.Transport.CreatePartitionProxy(
            firstReceiver.Address,
            GrainDirectoryPartition.CreateGrainId(source.Address, 0).GrainId);
        var retainedV2Task = probe.GetSnapshotAsync(
            new MembershipVersion(3),
            new MembershipVersion(2),
            secondTransferRange,
            cancellationToken).AsTask();
        var retainedV2Envelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(firstReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Range == secondTransferRange
                && envelope.Sequence != secondSnapshotEnvelope.Sequence,
            "retained v2 snapshot probe queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(retainedV2Envelope.Sequence),
            "deliver retained v2 snapshot probe",
            cancellationToken);
        var retainedV2 = Assert.IsType<GrainDirectoryPartitionSnapshot>(
            await fixture.GuardAsync(retainedV2Task, "retained v2 snapshot probe", cancellationToken));
        Assert.Equal(new MembershipVersion(2), retainedV2.DirectoryMembershipVersion);
        Assert.True(retainedV2Envelope.TargetRanOnDestinationScheduler);

        var currentResults = await fixture.GuardAsync(
            fixture.Transport.DeliverDuplicateAsync(currentAcknowledgement.Sequence),
            "deliver duplicate current acknowledgement",
            cancellationToken);
        Assert.True(Assert.IsType<bool>(currentResults.Original));
        Assert.True(Assert.IsType<bool>(currentResults.Duplicate));
        Assert.Equal(2, currentAcknowledgement.InvocationCount);
        Assert.Equal(1, currentAcknowledgement.CallerCompletionCount);
        Assert.Equal(RpcEnvelopeState.Duplicated, currentAcknowledgement.State);

        var removedV2Task = probe.GetSnapshotAsync(
            new MembershipVersion(3),
            new MembershipVersion(2),
            secondTransferRange,
            cancellationToken).AsTask();
        var removedV2Envelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(firstReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Sequence != retainedV2Envelope.Sequence
                && envelope.Sequence != secondSnapshotEnvelope.Sequence,
            "removed v2 snapshot probe queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(removedV2Envelope.Sequence),
            "deliver removed v2 snapshot probe",
            cancellationToken);
        Assert.Null(await fixture.GuardAsync(removedV2Task, "removed v2 snapshot probe", cancellationToken));

        var lookupTask = secondReceiver.Directory.Lookup(grainId, cancellationToken);
        var lookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(secondReceiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.GrainId == grainId,
            "final owner lookup queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(lookupEnvelope.Sequence),
            "deliver final owner lookup",
            cancellationToken);

        AssertAddress(expected, await fixture.GuardAsync(lookupTask, "final owner lookup", cancellationToken));
        AssertAddress(expected, await source.ProbeLocalAsync(grainId, cancellationToken));
        Assert.Null(await firstReceiver.ProbeLocalAsync(grainId, cancellationToken));
        Assert.Null(await secondReceiver.ProbeLocalAsync(grainId, cancellationToken));
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
        Assert.Contains(
            fixture.Trace,
            entry => entry.Action == $"rpc:duplicate:complete:{oldAcknowledgement.Sequence}");
        Assert.Contains(
            fixture.Trace,
            entry => entry.Action == $"rpc:duplicate:complete:{currentAcknowledgement.Sequence}");
    }

    [Fact]
    public async Task CrashAfterInstallationBeforeAcknowledgement_LateDuplicateAckCannotCorruptSuccessor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var installedOwnerAddress = SiloAddress.FromParsableString("127.0.0.1:18111@801");
        var replacementOwnerAddress = SiloAddress.FromParsableString("127.0.0.1:18111@804");
        var successorAddress = SiloAddress.FromParsableString("127.0.0.1:18112@802");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:18113@803");
        await using var fixture = new ControlledProtocolFixture(
            (installedOwnerAddress, 0x4000_0000u),
            (replacementOwnerAddress, 0x4000_0000u),
            (successorAddress, 0x4080_0000u),
            (sourceAddress, 0x4100_0000u));
        var installedOwner = fixture.GetNode(installedOwnerAddress);
        var replacementOwner = fixture.GetNode(replacementOwnerAddress);
        var successor = fixture.GetNode(successorAddress);
        var source = fixture.GetNode(sourceAddress);
        var sourceOnly = new[] { source };
        var sourceAndInstalled = new[] { source, installedOwner };
        var sourceAndReplacement = new[] { source, replacementOwner };
        var sourceAndSuccessor = new[] { source, successor };
        var transferRange = ToRingRange(fixture.Ownership.GetRange(
            installedOwner.Address,
            sourceAndInstalled.Select(static node => node.Address).ToHashSet()));
        var successorRange = ToRingRange(fixture.Ownership.GetRange(
            successor.Address,
            sourceAndSuccessor.Select(static node => node.Address).ToHashSet()));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);
        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "late-ack initial owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, sourceOnly, assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "late-ack initial owner acquire", cancellationToken);

        var grainId = FindGrainIdOwnedBy(
            fixture,
            successor.Address,
            "crash-after-installation",
            sourceAndSuccessor.Select(static node => node.Address).ToHashSet());
        var original = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("3c2acc4b-f546-4522-aa74-254036318011")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expected = fixture.Registrations.Register(
            original,
            previousAddress: null,
            new MembershipVersion(1),
            sourceOnly.Select(static node => node.Address).ToHashSet());
        fixture.ExpectedRecord = expected;
        AssertAddress(
            expected,
            await RegisterRemotelyAsync(
                fixture,
                successor,
                source,
                original,
                previousAddress: null,
                new MembershipVersion(1),
                "late-ack initial registration",
                cancellationToken));
        var recoveryContext = source.SeedRecoveryActivation(expected);

        var installedAcquireV2 = fixture.WaitForRangeOperationAsync(
            installedOwner,
            2,
            transferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "installed owner v2 acquire",
            cancellationToken);
        var sourceReleaseV2 = fixture.WaitForRangeOperationAsync(
            source,
            2,
            transferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "source v2 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(2, sourceAndInstalled, assertRangeCompletion: false, cancellationToken);
        var v1Snapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(installedOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == transferRange,
            "installed owner v1 snapshot",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v1Snapshot.Sequence),
            "deliver installed owner v1 snapshot",
            cancellationToken);
        await fixture.GuardAsync(installedAcquireV2, "installed owner v2 acquire complete", cancellationToken);
        await fixture.GuardAsync(sourceReleaseV2, "source v2 release complete", cancellationToken);
        var oldAcknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(installedOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(1),
            "hold installed owner v1 acknowledgement",
            cancellationToken);
        AssertAddress(expected, await installedOwner.ProbeLocalAsync(grainId, cancellationToken));

        var sourceAcquireV3 = fixture.WaitForRangeOperationAsync(
            source,
            3,
            transferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "source v3 reacquire",
            cancellationToken);
        var installedReleaseV3 = fixture.WaitForRangeOperationAsync(
            installedOwner,
            3,
            transferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "installed owner v3 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            3,
            observers: fixture.Nodes,
            activeNodes: sourceOnly,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken: cancellationToken,
            shuttingDownNodes: [installedOwner]);
        var v2Snapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(installedOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Range == transferRange,
            "source v2 reacquisition snapshot",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v2Snapshot.Sequence),
            "deliver source v2 reacquisition snapshot",
            cancellationToken);
        await fixture.GuardAsync(sourceAcquireV3, "source v3 reacquire complete", cancellationToken);
        await fixture.GuardAsync(installedReleaseV3, "installed owner v3 release complete", cancellationToken);
        var v2Acknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(installedOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(2),
            "source v2 acknowledgement",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v2Acknowledgement.Sequence),
            "deliver source v2 acknowledgement",
            cancellationToken);
        Assert.Same(recoveryContext, source.Activations.FindTarget(grainId));
        AssertAddress(expected, await source.ProbeLocalAsync(grainId, cancellationToken));

        var installedAcquireV4 = fixture.WaitForRangeOperationAsync(
            replacementOwner,
            4,
            transferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "replacement incarnation v4 acquire",
            cancellationToken);
        var sourceReleaseV4 = fixture.WaitForRangeOperationAsync(
            source,
            4,
            transferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "source v4 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            4,
            observers: fixture.Nodes,
            activeNodes: sourceAndReplacement,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken: cancellationToken,
            stoppingNodes: [installedOwner]);
        var v3Snapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(replacementOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.RangeVersion == new MembershipVersion(3)
                && envelope.Range == transferRange,
            "installed owner v3 snapshot",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v3Snapshot.Sequence),
            "deliver installed owner v3 snapshot",
            cancellationToken);
        await fixture.GuardAsync(installedAcquireV4, "installed owner v4 acquire complete", cancellationToken);
        await fixture.GuardAsync(sourceReleaseV4, "source v4 release complete", cancellationToken);
        var currentAcknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(replacementOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(3),
            "hold installed owner v3 acknowledgement",
            cancellationToken);
        Assert.Equal(RpcEnvelopeState.Queued, oldAcknowledgement.State);
        Assert.Equal(RpcEnvelopeState.Queued, currentAcknowledgement.State);
        AssertAddress(expected, await replacementOwner.ProbeLocalAsync(grainId, cancellationToken));
        Assert.Null(await installedOwner.ProbeLocalAsync(grainId, cancellationToken));

        var retainedSnapshotProbe = fixture.Transport.CreatePartitionProxy(
            successor.Address,
            GrainDirectoryPartition.CreateGrainId(source.Address, 0).GrainId);
        var retainedV1Task = retainedSnapshotProbe.GetSnapshotAsync(
            new MembershipVersion(4),
            new MembershipVersion(1),
            transferRange,
            cancellationToken).AsTask();
        var retainedV1Envelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == transferRange,
            "retained v1 snapshot probe",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(retainedV1Envelope.Sequence),
            "deliver retained v1 snapshot probe",
            cancellationToken);
        var retainedV1 = Assert.IsType<GrainDirectoryPartitionSnapshot>(
            await fixture.GuardAsync(retainedV1Task, "retained v1 snapshot response", cancellationToken));
        Assert.Equal(new MembershipVersion(1), retainedV1.DirectoryMembershipVersion);

        fixture.Transport.Crash(replacementOwner.Address);
        Assert.True(fixture.Transport.IsCrashed(replacementOwner.Address));
        Assert.False(replacementOwner.LifecycleStopCalled);
        Assert.Equal(RpcEnvelopeState.Queued, oldAcknowledgement.State);
        Assert.Equal(RpcEnvelopeState.Queued, currentAcknowledgement.State);

        var successorAcquireV5 = fixture.WaitForRangeOperationAsync(
            successor,
            5,
            successorRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "successor v5 acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            5,
            observers: [successor],
            activeNodes: sourceAndSuccessor,
            declaredDeadNodes: [replacementOwner],
            assertRangeCompletion: false,
            cancellationToken: cancellationToken,
            stoppingNodes: [installedOwner]);
        var successorRecovery = await DeliverRecoveryRequestsAsync(
            fixture,
            successor,
            new MembershipVersion(5),
            successorRange,
            expectedCount: 2,
            "successor v5 recovery",
            cancellationToken);
        Assert.Contains(
            successorRecovery,
            envelope => envelope.Destination.Equals(source.Address)
                && Assert.IsType<Immutable<List<GrainAddress>>>(envelope.Response).Value.Any(
                    address => address.GrainId == expected.GrainId
                        && address.ActivationId == expected.ActivationId));
        await fixture.GuardAsync(successorAcquireV5, "successor v5 acquire complete", cancellationToken);
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expected, sourceAndSuccessor, cancellationToken);

        var oldResults = await fixture.GuardAsync(
            fixture.Transport.DeliverDuplicateAsync(oldAcknowledgement.Sequence),
            "deliver old v1 acknowledgement twice",
            cancellationToken);
        Assert.True(Assert.IsType<bool>(oldResults.Original));
        Assert.True(Assert.IsType<bool>(oldResults.Duplicate));
        Assert.Equal(2, oldAcknowledgement.InvocationCount);
        Assert.Equal(1, oldAcknowledgement.CallerCompletionCount);
        Assert.Equal(RpcEnvelopeState.Duplicated, oldAcknowledgement.State);

        var removedV1Task = retainedSnapshotProbe.GetSnapshotAsync(
            new MembershipVersion(4),
            new MembershipVersion(1),
            transferRange,
            cancellationToken).AsTask();
        var removedV1Envelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == transferRange,
            "removed v1 snapshot probe",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(removedV1Envelope.Sequence),
            "deliver removed v1 snapshot probe",
            cancellationToken);
        Assert.Null(await fixture.GuardAsync(removedV1Task, "removed v1 snapshot response", cancellationToken));

        var retainedV3Task = retainedSnapshotProbe.GetSnapshotAsync(
            new MembershipVersion(4),
            new MembershipVersion(3),
            transferRange,
            cancellationToken).AsTask();
        var retainedV3Envelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.RangeVersion == new MembershipVersion(3)
                && envelope.Range == transferRange,
            "retained v3 snapshot probe",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(retainedV3Envelope.Sequence),
            "deliver retained v3 snapshot probe",
            cancellationToken);
        var retainedV3 = Assert.IsType<GrainDirectoryPartitionSnapshot>(
            await fixture.GuardAsync(retainedV3Task, "retained v3 snapshot response", cancellationToken));
        Assert.Equal(new MembershipVersion(3), retainedV3.DirectoryMembershipVersion);
        Assert.Equal(RpcEnvelopeState.Queued, currentAcknowledgement.State);

        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(currentAcknowledgement.Sequence),
            "deliver current v3 acknowledgement",
            cancellationToken);
        Assert.True(Assert.IsType<bool>(currentAcknowledgement.Response));
        Assert.Equal(RpcEnvelopeState.Delivered, currentAcknowledgement.State);

        var removedV3Task = retainedSnapshotProbe.GetSnapshotAsync(
            new MembershipVersion(4),
            new MembershipVersion(3),
            transferRange,
            cancellationToken).AsTask();
        var removedV3Envelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.RangeVersion == new MembershipVersion(3)
                && envelope.Range == transferRange
                && envelope.Sequence != retainedV3Envelope.Sequence,
            "removed v3 snapshot probe",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(removedV3Envelope.Sequence),
            "deliver removed v3 snapshot probe",
            cancellationToken);
        Assert.Null(await fixture.GuardAsync(removedV3Task, "removed v3 snapshot response", cancellationToken));

        var liveSilos = sourceAndSuccessor.Select(static node => node.Address).ToHashSet();
        AssertAddress(expected, fixture.Registrations.Lookup(grainId, liveSilos));
        AssertAddress(
            expected,
            await LookupRemotelyAsync(
                fixture,
                successor,
                successor,
                grainId,
                new MembershipVersion(5),
                "late-ack successor lookup",
                cancellationToken));
        await fixture.GuardAsync(
            successor.WaitForMembershipVersionAsync(new MembershipVersion(5), cancellationToken),
            "late-ack successor v5 membership gate",
            cancellationToken);
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expected, sourceAndSuccessor, cancellationToken);
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    [Theory]
    [InlineData(CapturedSnapshotOutcome.DeliverCapturedSnapshot)]
    [InlineData(CapturedSnapshotOutcome.FaultCapturedSnapshotAndRecover)]
    public async Task CrashAfterRetentionBeforeInstallation_ConvergesWithoutDuplicateOrStaleRegistration(
        CapturedSnapshotOutcome outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:17111@701");
        var receiverAddress = SiloAddress.FromParsableString("127.0.0.1:17112@702");
        var activationHostAddress = SiloAddress.FromParsableString("127.0.0.1:17113@703");
        await using var fixture = new ControlledProtocolFixture(
            (sourceAddress, 0x4000_0000u),
            (receiverAddress, 0x4080_0000u),
            (activationHostAddress, 0x4100_0000u));
        var source = fixture.GetNode(sourceAddress);
        var receiver = fixture.GetNode(receiverAddress);
        var activationHost = fixture.GetNode(activationHostAddress);
        var activeV1 = new[] { source, activationHost };
        var activeV2 = new[] { source, receiver, activationHost };
        var survivors = new[] { receiver, activationHost };
        var transferRange = ToRingRange(fixture.Ownership.GetRange(receiver.Address));
        var hostRecoveryRange = ToRingRange(fixture.Ownership.GetRange(
            source.Address,
            activeV2.Select(static node => node.Address).ToHashSet()));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);
        await fixture.PublishAndObserveAsync(1, activeV1, assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(
            Task.WhenAll(activeV1.Select(node => node.WaitForMembershipVersionAsync(
                new MembershipVersion(1), cancellationToken))),
            "retention initial ranges open",
            cancellationToken);

        var grainId = FindGrainIdOwnedBy(fixture, receiver.Address, "crash-after-retention");
        var original = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("0ba78fbf-851f-4349-b5fa-b83320307011")),
            SiloAddress = activationHost.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expectedV1 = fixture.Registrations.Register(
            original,
            previousAddress: null,
            new MembershipVersion(1),
            activeV1.Select(static node => node.Address).ToHashSet());
        AssertAddress(
            expectedV1,
            await RegisterRemotelyAsync(
                fixture,
                activationHost,
                source,
                original,
                previousAddress: null,
                new MembershipVersion(1),
                "retention initial registration",
                cancellationToken));
        var recoveryContext = activationHost.SeedRecoveryActivation(expectedV1);

        var sourceRelease = fixture.WaitForRangeOperationAsync(
            source,
            2,
            transferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "retention source v2 release",
            cancellationToken);
        var receiverAcquire = fixture.WaitForRangeOperationAsync(
            receiver,
            2,
            transferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "retention receiver v2 acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(2, activeV2, assertRangeCompletion: false, cancellationToken);
        var capturedEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == transferRange,
            "retention snapshot queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.CaptureResponseAsync(capturedEnvelope.Sequence),
            "capture retained snapshot",
            cancellationToken);
        var capturedSnapshot = Assert.IsType<GrainDirectoryPartitionSnapshot>(capturedEnvelope.Response);
        Assert.Equal(new MembershipVersion(1), capturedSnapshot.DirectoryMembershipVersion);
        Assert.Contains(capturedSnapshot.GrainAddresses, address => address.GrainId == grainId);
        Assert.Equal(RpcEnvelopeState.ResponseCaptured, capturedEnvelope.State);
        await fixture.GuardAsync(sourceRelease, "source retained v1 snapshot", cancellationToken);
        AssertRangeCompletionAbsent(
            fixture,
            receiver,
            new MembershipVersion(2),
            transferRange,
            GrainDirectoryEvents.AcquireOperationName);
        Assert.False(receiverAcquire.IsCompleted);

        fixture.Transport.Crash(source.Address);
        Assert.True(fixture.Transport.IsCrashed(source.Address));
        Assert.False(source.LifecycleStopCalled);
        Assert.Equal(RpcEnvelopeState.ResponseCaptured, capturedEnvelope.State);

        var hostAcquire = fixture.WaitForRangeOperationAsync(
            activationHost,
            3,
            hostRecoveryRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "retention host v3 recovery",
            cancellationToken);

        IReadOnlyList<RpcEnvelope> receiverRecovery = [];
        if (outcome == CapturedSnapshotOutcome.DeliverCapturedSnapshot)
        {
            fixture.Transport.ReleaseCapturedResponse(capturedEnvelope.Sequence);
            await fixture.GuardAsync(receiverAcquire, "install captured snapshot after source crash", cancellationToken);
            Assert.Equal(RpcEnvelopeState.Delivered, capturedEnvelope.State);
            await fixture.PublishAndObserveAsync(
                3,
                observers: survivors,
                activeNodes: survivors,
                declaredDeadNodes: [source],
                assertRangeCompletion: false,
                cancellationToken);
        }
        else
        {
            await fixture.PublishAndObserveAsync(
                3,
                observers: survivors,
                activeNodes: survivors,
                declaredDeadNodes: [source],
                assertRangeCompletion: false,
                cancellationToken);
            await fixture.Transport.FaultAsync(
                capturedEnvelope.Sequence,
                new SiloUnavailableException($"Silo {source.Address} failed after retaining the snapshot."));
            receiverRecovery = await DeliverRecoveryRequestsAsync(
                fixture,
                receiver,
                new MembershipVersion(2),
                transferRange,
                expectedCount: 2,
                "retention receiver fallback",
                cancellationToken);
            Assert.Contains(receiverRecovery, envelope => envelope.Destination.Equals(activationHost.Address));
            Assert.Contains(
                receiverRecovery,
                envelope => Assert.IsType<Immutable<List<GrainAddress>>>(envelope.Response).Value.Any(
                    address => address.GrainId == expectedV1.GrainId
                        && address.ActivationId == expectedV1.ActivationId));
            await fixture.GuardAsync(receiverAcquire, "receiver recovery after captured snapshot fault", cancellationToken);
            Assert.Equal(RpcEnvelopeState.Faulted, capturedEnvelope.State);
        }

        var hostRecovery = await DeliverRecoveryRequestsAsync(
            fixture,
            activationHost,
            new MembershipVersion(3),
            hostRecoveryRange,
            expectedCount: 2,
            "retention host recovery",
            cancellationToken);
        await fixture.GuardAsync(hostAcquire, "retention host v3 recovery completion", cancellationToken);
        await fixture.GuardAsync(
            receiver.WaitForMembershipVersionAsync(new MembershipVersion(3), cancellationToken),
            "retention receiver v3 membership gate",
            cancellationToken);
        Assert.Same(recoveryContext, activationHost.Activations.FindTarget(grainId));
        Assert.All(hostRecovery, envelope => Assert.Equal(RpcEnvelopeState.Delivered, envelope.State));
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expectedV1, survivors, cancellationToken);

        var replacement = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("35d8e197-58ec-49f0-8673-2190f7b77022")),
            SiloAddress = activationHost.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expectedV3 = fixture.Registrations.Register(
            replacement,
            expectedV1,
            new MembershipVersion(3),
            survivors.Select(static node => node.Address).ToHashSet());
        fixture.ExpectedRecord = expectedV3;
        AssertAddress(
            expectedV3,
            await RegisterRemotelyAsync(
                fixture,
                activationHost,
                receiver,
                replacement,
                expectedV1,
                new MembershipVersion(3),
                "retention valid replacement",
                cancellationToken));
        var staleResult = await RegisterRemotelyAsync(
            fixture,
            activationHost,
            receiver,
            original,
            previousAddress: null,
            new MembershipVersion(3),
            "retention stale registration",
            cancellationToken);
        AssertAddress(
            expectedV3,
            fixture.Registrations.Register(
                original,
                previousAddress: null,
                new MembershipVersion(3),
                survivors.Select(static node => node.Address).ToHashSet()));
        AssertAddress(expectedV3, staleResult);
        AssertAddress(
            expectedV3,
            await LookupRemotelyAsync(
                fixture,
                activationHost,
                receiver,
                grainId,
                new MembershipVersion(3),
                "retention final lookup",
                cancellationToken));
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expectedV3, survivors, cancellationToken);
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    [Fact]
    public async Task CrashBeforeRetention_RecoversFromSurvivingActivationAndEventuallyOpensRange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transientOwnerAddress = SiloAddress.FromParsableString("127.0.0.1:16111@601");
        var successorAddress = SiloAddress.FromParsableString("127.0.0.1:16112@602");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:16113@603");
        await using var fixture = new ControlledProtocolFixture(
            (transientOwnerAddress, 0x4000_0000u),
            (successorAddress, 0x4080_0000u),
            (sourceAddress, 0x4100_0000u));
        var transientOwner = fixture.GetNode(transientOwnerAddress);
        var successor = fixture.GetNode(successorAddress);
        var source = fixture.GetNode(sourceAddress);
        var activeV2 = new[] { source, transientOwner };
        var activeV3 = new[] { source, transientOwner, successor };
        var survivors = new[] { source, successor };
        var v2AcquireRange = ToRingRange(fixture.Ownership.GetRange(
            transientOwner.Address,
            activeV2.Select(static node => node.Address).ToHashSet()));
        var v3ReleaseRange = ToRingRange(fixture.Ownership.GetRange(successor.Address));
        var sourceRecoveryRange = ToRingRange(fixture.Ownership.GetRange(
            transientOwner.Address,
            activeV3.Select(static node => node.Address).ToHashSet()));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);
        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "before-retention initial owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, [source], assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "before-retention initial owner acquire", cancellationToken);

        var grainId = FindGrainIdOwnedBy(fixture, successor.Address, "crash-before-retention");
        var proposed = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("0ac02e85-f434-47bc-8ef1-b0457e0a6011")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expected = fixture.Registrations.Register(
            proposed,
            previousAddress: null,
            new MembershipVersion(1),
            new HashSet<SiloAddress> { source.Address });
        fixture.ExpectedRecord = expected;
        AssertAddress(
            expected,
            await RegisterRemotelyAsync(
                fixture,
                successor,
                source,
                proposed,
                previousAddress: null,
                new MembershipVersion(1),
                "before-retention initial registration",
                cancellationToken));
        var recoveryContext = source.SeedRecoveryActivation(expected);

        var v2Acquire = fixture.WaitForRangeOperationAsync(
            transientOwner,
            2,
            v2AcquireRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "transient owner v2 acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(2, activeV2, assertRangeCompletion: false, cancellationToken);
        var heldV2Snapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(transientOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == v2AcquireRange,
            "hold transient owner v2 acquisition",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.CaptureResponseAsync(heldV2Snapshot.Sequence),
            "capture transient owner v2 snapshot",
            cancellationToken);
        Assert.IsType<GrainDirectoryPartitionSnapshot>(heldV2Snapshot.Response);
        Assert.False(v2Acquire.IsCompleted);

        var releaseStarted = fixture.WaitForRangeOperationStartedAsync(
            transientOwner,
            3,
            v3ReleaseRange,
            GrainDirectoryEvents.ReleaseOperationName,
            "transient owner v3 release started before retention",
            cancellationToken);
        var successorAcquire = fixture.WaitForRangeOperationAsync(
            successor,
            3,
            v3ReleaseRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "successor v3 acquire after crash",
            cancellationToken);
        await fixture.PublishAndObserveAsync(3, activeV3, assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(releaseStarted, "transient owner v3 release start", cancellationToken);
        var notRetainedSnapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(transientOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Range == v3ReleaseRange,
            "before-retention snapshot request queued",
            cancellationToken);
        Assert.Equal(RpcEnvelopeState.Queued, notRetainedSnapshot.State);
        Assert.DoesNotContain(
            fixture.Transport.Envelopes,
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(transientOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.State == RpcEnvelopeState.ResponseCaptured);
        AssertRangeCompletionAbsent(
            fixture,
            transientOwner,
            new MembershipVersion(3),
            v3ReleaseRange,
            GrainDirectoryEvents.ReleaseOperationName);

        fixture.Transport.Crash(transientOwner.Address, settlePending: false);
        Assert.True(fixture.Transport.IsCrashed(transientOwner.Address));
        Assert.False(transientOwner.LifecycleStopCalled);
        var rejectedProbe = fixture.Transport.CreatePartitionProxy(
                source.Address,
                GrainDirectoryPartition.CreateGrainId(transientOwner.Address, 0).GrainId)
            .LookupAsync(new MembershipVersion(3), grainId, cancellationToken)
            .AsTask();
        await Assert.ThrowsAsync<SiloUnavailableException>(() => rejectedProbe);

        var sourceAcquire = fixture.WaitForRangeOperationAsync(
            source,
            4,
            sourceRecoveryRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "source v4 recovery acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            4,
            observers: survivors,
            activeNodes: survivors,
            declaredDeadNodes: [transientOwner],
            assertRangeCompletion: false,
            cancellationToken);
        await fixture.Transport.FaultAsync(
            notRetainedSnapshot.Sequence,
            new SiloUnavailableException($"Silo {transientOwner.Address} crashed before retention."));
        await fixture.Transport.DropAsync(
            heldV2Snapshot.Sequence,
            new SiloUnavailableException($"Captured bytes targeted crashed silo {transientOwner.Address}."));

        var blockedLookup = source.Directory.Lookup(grainId, cancellationToken);
        var blockedLookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(successor.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.GrainId == grainId,
            "before-retention blocked successor lookup",
            cancellationToken);
        // Hold the reply until both recovery scans have advanced the caller's watermark.
        var blockedLookupDelivery = fixture.Transport.CaptureResponseAsync(blockedLookupEnvelope.Sequence);
        Assert.False(blockedLookup.IsCompleted);
        Assert.False(blockedLookupDelivery.IsCompleted);
        AssertRangeCompletionAbsent(
            fixture,
            successor,
            new MembershipVersion(3),
            v3ReleaseRange,
            GrainDirectoryEvents.AcquireOperationName);

        var successorRecovery = await DeliverRecoveryRequestsAsync(
            fixture,
            successor,
            new MembershipVersion(3),
            v3ReleaseRange,
            expectedCount: 2,
            "before-retention successor",
            cancellationToken);
        var sourceRecovery = await DeliverRecoveryRequestsAsync(
            fixture,
            source,
            new MembershipVersion(4),
            sourceRecoveryRange,
            expectedCount: 2,
            "before-retention source",
            cancellationToken);
        Assert.Contains(
            successorRecovery,
            envelope => envelope.Destination.Equals(source.Address)
                && Assert.IsType<Immutable<List<GrainAddress>>>(envelope.Response).Value.Any(
                    address => address.GrainId == expected.GrainId
                        && address.ActivationId == expected.ActivationId));
        Assert.Same(recoveryContext, source.Activations.FindTarget(grainId));

        await fixture.GuardAsync(successorAcquire, "successor v3 recovery completion", cancellationToken);
        await fixture.GuardAsync(sourceAcquire, "source v4 recovery completion", cancellationToken);
        await fixture.GuardAsync(blockedLookupDelivery, "before-retention blocked lookup delivery", cancellationToken);
        Assert.Equal(4, source.Directory.RecoveryMembershipVersion);
        Assert.False(blockedLookup.IsCompleted);
        fixture.Transport.ReleaseCapturedResponse(blockedLookupEnvelope.Sequence);
        var retryLookup = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(successor.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.GrainId == grainId
                && envelope.Sequence != blockedLookupEnvelope.Sequence,
            "before-retention lookup retry",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(retryLookup.Sequence),
            "deliver before-retention lookup retry",
            cancellationToken);
        var routed = await fixture.GuardAsync(blockedLookup, "before-retention final lookup", cancellationToken);
        fixture.ActualRecord = routed;
        AssertAddress(expected, routed);
        await fixture.GuardAsync(
            successor.WaitForMembershipVersionAsync(new MembershipVersion(4), cancellationToken),
            "successor v4 membership gate",
            cancellationToken);
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expected, survivors, cancellationToken);
        Assert.All(sourceRecovery, envelope => Assert.Equal(RpcEnvelopeState.Delivered, envelope.State));
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    [Fact]
    public async Task DelayedStaleSnapshotAcrossNewerView_SerializesOverlappingHandoffs_AndPreservesNewerRegistration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var receiverAddress = SiloAddress.FromParsableString("127.0.0.1:15111@501");
        var successorAddress = SiloAddress.FromParsableString("127.0.0.1:15112@502");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:15113@503");
        await using var fixture = new ControlledProtocolFixture(
            (receiverAddress, 0x4000_0000u),
            (successorAddress, 0x4080_0000u),
            (sourceAddress, 0x4100_0000u));
        var receiver = fixture.GetNode(receiverAddress);
        var successor = fixture.GetNode(successorAddress);
        var source = fixture.GetNode(sourceAddress);
        var activeV2 = new[] { source, receiver };
        var activeV3 = new[] { source, receiver, successor };
        var v2AcquireRange = ToRingRange(fixture.Ownership.GetRange(
            receiver.Address,
            activeV2.Select(static node => node.Address).ToHashSet()));
        var v3TransferRange = ToRingRange(fixture.Ownership.GetRange(successor.Address));
        Assert.True(v2AcquireRange.Intersects(v3TransferRange));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);
        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "overlap initial owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, [source], assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "overlap initial owner acquire", cancellationToken);

        var grainId = FindGrainIdOwnedBy(fixture, successor.Address, "overlapping-handoff");
        var original = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("d5d43699-8eb2-4c30-bebc-16d76d8a5011")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expectedV1 = fixture.Registrations.Register(
            original,
            previousAddress: null,
            new MembershipVersion(1),
            new HashSet<SiloAddress> { source.Address });
        AssertAddress(
            expectedV1,
            await RegisterRemotelyAsync(
                fixture,
                receiver,
                source,
                original,
                previousAddress: null,
                new MembershipVersion(1),
                "overlap initial registration",
                cancellationToken));

        var v2AcquireStarted = fixture.WaitForRangeOperationStartedAsync(
            receiver,
            2,
            v2AcquireRange,
            GrainDirectoryEvents.AcquireOperationName,
            "receiver v2 acquire started",
            cancellationToken);
        var v2AcquireCompleted = fixture.WaitForRangeOperationAsync(
            receiver,
            2,
            v2AcquireRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "receiver v2 acquire completed",
            cancellationToken);
        var v2ReleaseCompleted = fixture.WaitForRangeOperationAsync(
            source,
            2,
            v2AcquireRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "source v2 release completed",
            cancellationToken);
        await fixture.PublishAndObserveAsync(2, activeV2, assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(v2AcquireStarted, "receiver v2 acquire start", cancellationToken);
        var staleSnapshotEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == v2AcquireRange,
            "capture delayed v1 snapshot",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.CaptureResponseAsync(staleSnapshotEnvelope.Sequence),
            "capture delayed v1 snapshot response",
            cancellationToken);
        var staleSnapshot = Assert.IsType<GrainDirectoryPartitionSnapshot>(staleSnapshotEnvelope.Response);
        Assert.Contains(staleSnapshot.GrainAddresses, address => address.GrainId == grainId);
        Assert.Equal(RpcEnvelopeState.ResponseCaptured, staleSnapshotEnvelope.State);
        await AssertAtMostOneLocalRegistrationAsync(fixture, grainId, fixture.Nodes, cancellationToken);

        var v3ReleaseStarted = fixture.WaitForRangeOperationStartedAsync(
            receiver,
            3,
            v3TransferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            "receiver v3 release started",
            cancellationToken);
        var v3ReleaseCompleted = fixture.WaitForRangeOperationAsync(
            receiver,
            3,
            v3TransferRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "receiver v3 release completed",
            cancellationToken);
        var v3AcquireStarted = fixture.WaitForRangeOperationStartedAsync(
            successor,
            3,
            v3TransferRange,
            GrainDirectoryEvents.AcquireOperationName,
            "successor v3 acquire started",
            cancellationToken);
        var v3AcquireCompleted = fixture.WaitForRangeOperationAsync(
            successor,
            3,
            v3TransferRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "successor v3 acquire completed",
            cancellationToken);
        await fixture.PublishAndObserveAsync(3, activeV3, assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(v3ReleaseStarted, "receiver v3 release start", cancellationToken);
        await fixture.GuardAsync(v3AcquireStarted, "successor v3 acquire start", cancellationToken);
        var v3SnapshotEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(receiver.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Range == v3TransferRange,
            "successor v3 snapshot queued",
            cancellationToken);
        Assert.Equal(RpcEnvelopeState.ResponseCaptured, staleSnapshotEnvelope.State);
        AssertRangeCompletionAbsent(
            fixture,
            receiver,
            new MembershipVersion(3),
            v3TransferRange,
            GrainDirectoryEvents.ReleaseOperationName);
        AssertRangeCompletionAbsent(
            fixture,
            successor,
            new MembershipVersion(3),
            v3TransferRange,
            GrainDirectoryEvents.AcquireOperationName);
        await AssertAtMostOneLocalRegistrationAsync(fixture, grainId, fixture.Nodes, cancellationToken);

        fixture.Transport.ReleaseCapturedResponse(staleSnapshotEnvelope.Sequence);
        await fixture.GuardAsync(v2AcquireCompleted, "receiver v2 acquire after delayed response", cancellationToken);
        await fixture.GuardAsync(v2ReleaseCompleted, "source v2 release after delayed response", cancellationToken);
        await AssertAtMostOneLocalRegistrationAsync(fixture, grainId, fixture.Nodes, cancellationToken);
        var v2Acknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(1),
            "overlap v2 acknowledgement",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v2Acknowledgement.Sequence),
            "deliver overlap v2 acknowledgement",
            cancellationToken);

        await fixture.GuardAsync(v3ReleaseCompleted, "receiver v3 release after v2 completion", cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v3SnapshotEnvelope.Sequence),
            "deliver serialized v3 snapshot",
            cancellationToken);
        await fixture.GuardAsync(v3AcquireCompleted, "successor v3 acquire complete", cancellationToken);
        var v3Acknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(successor.Address)
                && envelope.Destination.Equals(receiver.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(2),
            "overlap v3 acknowledgement",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v3Acknowledgement.Sequence),
            "deliver overlap v3 acknowledgement",
            cancellationToken);

        var completionPayloads = fixture.DirectoryEvents
            .GetEvents(nameof(GrainDirectoryEvents.RangeOperationCompleted))
            .Select(static diagnostic => diagnostic.Payload)
            .ToArray();
        var v2CompletionIndex = Array.FindIndex(
            completionPayloads,
            payload => payload is GrainDirectoryEvents.RangeOperationCompleted completion
                && completion.SiloAddress.Equals(receiver.Address)
                && completion.Version == new MembershipVersion(2)
                && completion.Range == v2AcquireRange
                && completion.OperationName == GrainDirectoryEvents.AcquireOperationName);
        var v3CompletionIndex = Array.FindIndex(
            completionPayloads,
            payload => payload is GrainDirectoryEvents.RangeOperationCompleted completion
                && completion.SiloAddress.Equals(receiver.Address)
                && completion.Version == new MembershipVersion(3)
                && completion.Range == v3TransferRange
                && completion.OperationName == GrainDirectoryEvents.ReleaseOperationName);
        Assert.InRange(v2CompletionIndex, 0, completionPayloads.Length - 1);
        Assert.True(v2CompletionIndex < v3CompletionIndex, fixture.DescribeState("overlap completion order"));
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expectedV1, activeV3, cancellationToken);

        var replacement = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("f1f1768c-ce10-4f57-a8dc-c089ed605022")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expectedV3 = fixture.Registrations.Register(
            replacement,
            expectedV1,
            new MembershipVersion(3),
            activeV3.Select(static node => node.Address).ToHashSet());
        fixture.ExpectedRecord = expectedV3;
        AssertAddress(
            expectedV3,
            await RegisterRemotelyAsync(
                fixture,
                source,
                successor,
                replacement,
                expectedV1,
                new MembershipVersion(3),
                "valid v3 replacement",
                cancellationToken));

        var staleResult = await RegisterRemotelyAsync(
            fixture,
            receiver,
            successor,
            original,
            previousAddress: null,
            new MembershipVersion(3),
            "stale registration replay",
            cancellationToken);
        var oracleAfterStale = fixture.Registrations.Register(
            original,
            previousAddress: null,
            new MembershipVersion(3),
            activeV3.Select(static node => node.Address).ToHashSet());
        AssertAddress(expectedV3, oracleAfterStale);
        AssertAddress(expectedV3, staleResult);
        AssertAddress(
            expectedV3,
            await LookupRemotelyAsync(
                fixture,
                source,
                successor,
                grainId,
                new MembershipVersion(3),
                "overlap final lookup",
                cancellationToken));
        await fixture.GuardAsync(
            successor.WaitForMembershipVersionAsync(new MembershipVersion(3), cancellationToken),
            "successor v3 membership gate",
            cancellationToken);
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expectedV3, activeV3, cancellationToken);
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    [Fact]
    public async Task SkippedMembershipView_UsesRecoveryWithoutPredecessorSnapshot_AndConverges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var skippedOwnerAddress = SiloAddress.FromParsableString("127.0.0.1:14111@401");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:14112@402");
        var intermediateOwnerAddress = SiloAddress.FromParsableString("127.0.0.1:14113@403");
        await using var fixture = CreateThreeNodeFixture(
            intermediateOwnerAddress,
            sourceAddress,
            skippedOwnerAddress);
        var skippedOwner = fixture.GetNode(skippedOwnerAddress);
        var source = fixture.GetNode(sourceAddress);
        var intermediateOwner = fixture.GetNode(intermediateOwnerAddress);
        var activeV2 = new[] { source, intermediateOwner };
        var activeV3 = new[] { source, intermediateOwner, skippedOwner };
        var intermediateRange = ToRingRange(fixture.Ownership.GetRange(
            intermediateOwner.Address,
            activeV2.Select(static node => node.Address).ToHashSet()));
        var skippedRange = ToRingRange(fixture.Ownership.GetRange(skippedOwner.Address));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);

        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "skipped-view initial owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, [source], assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "skipped-view initial owner acquire", cancellationToken);

        var grainId = FindGrainIdOwnedBy(fixture, skippedOwner.Address, "skipped-view");
        var proposed = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("2a188d6f-4e25-4b30-af3d-d606ac0a4011")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var liveV1 = new HashSet<SiloAddress> { source.Address };
        var expected = fixture.Registrations.Register(proposed, null, new MembershipVersion(1), liveV1);
        fixture.ExpectedRecord = expected;
        AssertAddress(
            expected,
            await RegisterRemotelyAsync(
                fixture,
                intermediateOwner,
                source,
                proposed,
                previousAddress: null,
                new MembershipVersion(1),
                "skipped-view initial registration",
                cancellationToken));
        var recoveryContext = source.SeedRecoveryActivation(expected);
        Assert.Same(recoveryContext, source.Activations.FindTarget(grainId));

        var v2Acquire = fixture.WaitForRangeOperationAsync(
            intermediateOwner,
            2,
            intermediateRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "intermediate v2 acquire",
            cancellationToken);
        var v2Release = fixture.WaitForRangeOperationAsync(
            source,
            2,
            intermediateRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "source v2 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            2,
            observers: [source, intermediateOwner],
            activeNodes: activeV2,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken);
        var v2Snapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(intermediateOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == intermediateRange,
            "intermediate v2 snapshot",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v2Snapshot.Sequence),
            "deliver intermediate v2 snapshot",
            cancellationToken);
        await fixture.GuardAsync(v2Acquire, "intermediate v2 acquire complete", cancellationToken);
        await fixture.GuardAsync(v2Release, "source v2 release complete", cancellationToken);
        var v2Ack = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(intermediateOwner.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(1),
            "intermediate v2 acknowledgement",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v2Ack.Sequence),
            "deliver intermediate v2 acknowledgement",
            cancellationToken);

        var sourceV3Release = fixture.WaitForRangeOperationAsync(
            source,
            3,
            skippedRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "source v3 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            3,
            observers: [source, intermediateOwner],
            activeNodes: activeV3,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken);
        await fixture.GuardAsync(sourceV3Release, "source v3 release complete", cancellationToken);

        var skippedAcquireStarted = fixture.WaitForRangeOperationStartedAsync(
            skippedOwner,
            3,
            skippedRange,
            GrainDirectoryEvents.AcquireOperationName,
            "skipped owner v3 acquire started",
            cancellationToken);
        var skippedAcquireCompleted = fixture.WaitForRangeOperationAsync(
            skippedOwner,
            3,
            skippedRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "skipped owner v3 acquire completed",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            3,
            observers: [skippedOwner],
            activeNodes: activeV3,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken);
        await fixture.GuardAsync(skippedAcquireStarted, "skipped owner v3 acquire start", cancellationToken);

        var blockedLookup = source.Directory.Lookup(grainId, cancellationToken);
        var blockedLookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(skippedOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.GrainId == grainId,
            "skipped-view blocked lookup",
            cancellationToken);
        var blockedLookupDelivery = fixture.Transport.DeliverAsync(blockedLookupEnvelope.Sequence);
        Assert.False(blockedLookup.IsCompleted);
        Assert.False(blockedLookupDelivery.IsCompleted);
        AssertRangeCompletionAbsent(
            fixture,
            skippedOwner,
            new MembershipVersion(3),
            skippedRange,
            GrainDirectoryEvents.AcquireOperationName);
        Assert.DoesNotContain(
            fixture.Transport.Envelopes,
            envelope => envelope.Source.Equals(skippedOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.RangeVersion == new MembershipVersion(2)
                && envelope.Range == skippedRange);

        var recoveryEnvelopes = await DeliverRecoveryRequestsAsync(
            fixture,
            skippedOwner,
            new MembershipVersion(3),
            skippedRange,
            expectedCount: 3,
            "skipped-view",
            cancellationToken);
        var sourceRecovery = Assert.Single(recoveryEnvelopes, envelope => envelope.Destination.Equals(source.Address));
        var recovered = Assert.IsType<Immutable<List<GrainAddress>>>(sourceRecovery.Response);
        Assert.Contains(recovered.Value, address =>
            address.GrainId == expected.GrainId
            && address.ActivationId == expected.ActivationId
            && address.SiloAddress!.Equals(expected.SiloAddress));

        await fixture.GuardAsync(skippedAcquireCompleted, "skipped owner v3 acquire complete", cancellationToken);
        await fixture.GuardAsync(blockedLookupDelivery, "unblock skipped-view lookup delivery", cancellationToken);
        var retryLookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(skippedOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(3)
                && envelope.GrainId == grainId
                && envelope.Sequence != blockedLookupEnvelope.Sequence,
            "skipped-view recovery-barrier lookup retry",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(retryLookupEnvelope.Sequence),
            "deliver skipped-view recovery-barrier lookup retry",
            cancellationToken);
        var routed = await fixture.GuardAsync(blockedLookup, "unblock skipped-view lookup", cancellationToken);
        fixture.ActualRecord = routed;
        AssertAddress(expected, routed);
        await fixture.GuardAsync(
            skippedOwner.WaitForMembershipVersionAsync(new MembershipVersion(3), cancellationToken),
            "skipped owner v3 membership gate",
            cancellationToken);
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expected, activeV3, cancellationToken);
        Assert.DoesNotContain(
            fixture.Transport.Envelopes,
            envelope => envelope.Source.Equals(skippedOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync));
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    [Fact]
    public async Task SamePartitionNewerSnapshot_WaitsForOlderOverlappingAcquisitionBeforeInstallingAndServing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var receiverAddress = SiloAddress.FromParsableString("127.0.0.1:19111@901");
        var transientOwnerAddress = SiloAddress.FromParsableString("127.0.0.1:19112@902");
        var sourceAddress = SiloAddress.FromParsableString("127.0.0.1:19113@903");
        await using var fixture = new ControlledProtocolFixture(
            (receiverAddress, 0x4000_0000u),
            (transientOwnerAddress, 0x4080_0000u),
            (sourceAddress, 0x4100_0000u));
        var receiver = fixture.GetNode(receiverAddress);
        var transientOwner = fixture.GetNode(transientOwnerAddress);
        var source = fixture.GetNode(sourceAddress);
        var sourceOnly = new[] { source };
        var activeV2 = new[] { source, receiver };
        var activeV3 = new[] { source, receiver, transientOwner };
        var receiverRangeV2 = ToRingRange(fixture.Ownership.GetRange(
            receiver.Address,
            activeV2.Select(static node => node.Address).ToHashSet()));
        var transferredRange = ToRingRange(fixture.Ownership.GetRange(
            transientOwner.Address,
            activeV3.Select(static node => node.Address).ToHashSet()));
        Assert.True(receiverRangeV2.Intersects(transferredRange));

        await fixture.StartAsync(cancellationToken);
        await fixture.PublishAndObserveAsync(0, [], assertRangeCompletion: false, cancellationToken);
        var initialAcquire = fixture.WaitForRangeOperationAsync(
            source,
            1,
            RingRange.Full,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "same-partition initial owner acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(1, sourceOnly, assertRangeCompletion: false, cancellationToken);
        await fixture.GuardAsync(initialAcquire, "same-partition initial owner acquire", cancellationToken);

        var grainId = FindGrainIdOwnedBy(
            fixture,
            transientOwner.Address,
            "same-partition-transfer-wait",
            activeV3.Select(static node => node.Address).ToHashSet());
        Assert.True(receiverRangeV2.Contains(grainId));
        Assert.True(transferredRange.Contains(grainId));
        var stale = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("12ab3b9b-a806-4737-9ee6-7466ae0a9011")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expectedV1 = fixture.Registrations.Register(
            stale,
            previousAddress: null,
            new MembershipVersion(1),
            sourceOnly.Select(static node => node.Address).ToHashSet());
        AssertAddress(
            expectedV1,
            await RegisterRemotelyAsync(
                fixture,
                receiver,
                source,
                stale,
                previousAddress: null,
                new MembershipVersion(1),
                "same-partition stale registration",
                cancellationToken));
        var recoveryContext = source.SeedRecoveryActivation(expectedV1);

        var receiverAcquireV2 = fixture.WaitForRangeOperationAsync(
            receiver,
            2,
            receiverRangeV2,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "same receiver v2 acquire",
            cancellationToken);
        var sourceReleaseV2 = fixture.WaitForRangeOperationAsync(
            source,
            2,
            receiverRangeV2,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "same-partition source v2 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            2,
            observers: activeV2,
            activeNodes: activeV2,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken);
        var heldV2Snapshot = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(2)
                && envelope.RangeVersion == new MembershipVersion(1)
                && envelope.Range == receiverRangeV2,
            "same receiver held v2 snapshot",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.CaptureResponseAsync(heldV2Snapshot.Sequence),
            "capture same receiver v2 snapshot",
            cancellationToken);
        var staleSnapshot = Assert.IsType<GrainDirectoryPartitionSnapshot>(heldV2Snapshot.Response);
        Assert.Contains(staleSnapshot.GrainAddresses, address =>
            address.GrainId == expectedV1.GrainId
            && address.ActivationId == expectedV1.ActivationId);
        await fixture.GuardAsync(sourceReleaseV2, "same-partition source v2 release", cancellationToken);
        Assert.False(receiverAcquireV2.IsCompleted);
        Assert.Null(await receiver.ProbeLocalAsync(grainId, cancellationToken));

        var receiverReleaseV3 = fixture.WaitForRangeOperationAsync(
            receiver,
            3,
            transferredRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "same receiver v3 release",
            cancellationToken);
        var receiverReleaseV3Started = fixture.WaitForRangeOperationStartedAsync(
            receiver,
            3,
            transferredRange,
            GrainDirectoryEvents.ReleaseOperationName,
            "same receiver v3 release started",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            3,
            observers: activeV2,
            activeNodes: activeV3,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken);
        await fixture.GuardAsync(
            receiverReleaseV3Started,
            "same receiver v3 release start",
            cancellationToken);
        Assert.False(receiverReleaseV3.IsCompleted);

        var transientAcquireV3 = fixture.WaitForRangeOperationAsync(
            transientOwner,
            3,
            transferredRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "transient owner v3 recovery acquire",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            3,
            observers: [transientOwner],
            activeNodes: activeV3,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken);
        var recoveryEnvelopes = await DeliverRecoveryRequestsAsync(
            fixture,
            transientOwner,
            new MembershipVersion(3),
            transferredRange,
            expectedCount: 3,
            "same-partition transient owner",
            cancellationToken);
        var sourceRecovery = Assert.Single(
            recoveryEnvelopes,
            envelope => envelope.Destination.Equals(source.Address));
        Assert.Contains(
            Assert.IsType<Immutable<List<GrainAddress>>>(sourceRecovery.Response).Value,
            address => address.GrainId == expectedV1.GrainId
                && address.ActivationId == expectedV1.ActivationId
                && address.MembershipVersion == expectedV1.MembershipVersion);
        await fixture.GuardAsync(
            transientAcquireV3,
            "transient owner v3 recovery acquire completion",
            cancellationToken);
        Assert.Same(recoveryContext, source.Activations.FindTarget(grainId));
        AssertAddress(expectedV1, await transientOwner.ProbeLocalAsync(grainId, cancellationToken));
        var replacement = new GrainAddress
        {
            GrainId = grainId,
            ActivationId = new ActivationId(Guid.Parse("442e83e9-a8f6-4e64-a28a-e4da52749022")),
            SiloAddress = source.Address,
            MembershipVersion = MembershipVersion.MinValue,
        };
        var expectedV3 = fixture.Registrations.Register(
            replacement,
            expectedV1,
            new MembershipVersion(3),
            activeV3.Select(static node => node.Address).ToHashSet());
        AssertAddress(
            expectedV3,
            await RegisterRemotelyAsync(
                fixture,
                source,
                transientOwner,
                replacement,
                expectedV1,
                new MembershipVersion(3),
                "same-partition accepted v3 replacement",
                cancellationToken));
        Assert.True(source.Activations.RemoveTarget(recoveryContext));
        source.SeedRecoveryActivation(expectedV3);
        AssertAddress(expectedV3, await transientOwner.ProbeLocalAsync(grainId, cancellationToken));
        Assert.False(receiverAcquireV2.IsCompleted);
        Assert.False(receiverReleaseV3.IsCompleted);

        var receiverAcquireV4Started = fixture.WaitForRangeOperationStartedAsync(
            receiver,
            4,
            transferredRange,
            GrainDirectoryEvents.AcquireOperationName,
            "same receiver overlapping v4 acquire started",
            cancellationToken);
        var receiverAcquireV4 = fixture.WaitForRangeOperationAsync(
            receiver,
            4,
            transferredRange,
            GrainDirectoryEvents.AcquireOperationName,
            canceled: false,
            "same receiver overlapping v4 acquire",
            cancellationToken);
        var transientReleaseV4 = fixture.WaitForRangeOperationAsync(
            transientOwner,
            4,
            transferredRange,
            GrainDirectoryEvents.ReleaseOperationName,
            canceled: false,
            "transient owner v4 release",
            cancellationToken);
        await fixture.PublishAndObserveAsync(
            4,
            observers: fixture.Nodes,
            activeNodes: activeV2,
            declaredDeadNodes: [],
            assertRangeCompletion: false,
            cancellationToken: cancellationToken,
            shuttingDownNodes: [transientOwner]);
        await fixture.GuardAsync(
            receiverAcquireV4Started,
            "same receiver overlapping v4 acquire start",
            cancellationToken);
        var newerSnapshotEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(transientOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.GetSnapshotAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.RangeVersion == new MembershipVersion(3)
                && envelope.Range == transferredRange,
            "same receiver newer snapshot queued",
            cancellationToken);
        await fixture.GuardAsync(transientReleaseV4, "transient owner v4 release completion", cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.CaptureResponseAsync(newerSnapshotEnvelope.Sequence),
            "capture same receiver newer snapshot",
            cancellationToken);
        var newerSnapshot = Assert.IsType<GrainDirectoryPartitionSnapshot>(newerSnapshotEnvelope.Response);
        Assert.Contains(newerSnapshot.GrainAddresses, address =>
            address.GrainId == expectedV3.GrainId
            && address.ActivationId == expectedV3.ActivationId
            && address.MembershipVersion == expectedV3.MembershipVersion);

        fixture.Transport.ReleaseCapturedResponse(newerSnapshotEnvelope.Sequence);
        Assert.Null(await receiver.ProbeLocalAsync(grainId, cancellationToken));
        Assert.False(receiverAcquireV2.IsCompleted);
        Assert.False(receiverReleaseV3.IsCompleted);
        Assert.False(receiverAcquireV4.IsCompleted);
        AssertRangeCompletionAbsent(
            fixture,
            receiver,
            new MembershipVersion(4),
            transferredRange,
            GrainDirectoryEvents.AcquireOperationName);

        var blockedLookup = source.Directory.Lookup(grainId, cancellationToken);
        var blockedLookupEnvelope = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(source.Address)
                && envelope.Destination.Equals(receiver.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && envelope.MembershipVersion == new MembershipVersion(4)
                && envelope.GrainId == grainId,
            "same receiver blocked v4 lookup",
            cancellationToken);
        var blockedLookupDelivery = fixture.Transport.DeliverAsync(blockedLookupEnvelope.Sequence);
        Assert.Null(await receiver.ProbeLocalAsync(grainId, cancellationToken));
        Assert.False(blockedLookupDelivery.IsCompleted);
        Assert.False(blockedLookup.IsCompleted);

        fixture.Transport.ReleaseCapturedResponse(heldV2Snapshot.Sequence);
        await fixture.GuardAsync(receiverAcquireV2, "same receiver v2 acquire completion", cancellationToken);
        await fixture.GuardAsync(receiverReleaseV3, "same receiver v3 release completion", cancellationToken);
        await fixture.GuardAsync(receiverAcquireV4, "same receiver v4 acquire completion", cancellationToken);
        await fixture.GuardAsync(
            blockedLookupDelivery,
            "same receiver v4 lookup delivery completion",
            cancellationToken);
        var lookupResult = await fixture.GuardAsync(
            blockedLookup,
            "same receiver eventual lookup",
            cancellationToken);
        fixture.ExpectedRecord = expectedV3;
        fixture.ActualRecord = lookupResult;
        AssertAddress(expectedV3, lookupResult);

        var v2Acknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(source.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(1),
            "same receiver v2 acknowledgement",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v2Acknowledgement.Sequence),
            "deliver same receiver v2 acknowledgement",
            cancellationToken);
        var v4Acknowledgement = await fixture.Transport.WaitForQueuedAsync(
            envelope => envelope.Source.Equals(receiver.Address)
                && envelope.Destination.Equals(transientOwner.Address)
                && envelope.Operation == nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                && envelope.RangeVersion == new MembershipVersion(3),
            "same receiver v4 acknowledgement",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(v4Acknowledgement.Sequence),
            "deliver same receiver v4 acknowledgement",
            cancellationToken);

        var completionPayloads = fixture.DirectoryEvents
            .GetEvents(nameof(GrainDirectoryEvents.RangeOperationCompleted))
            .Select(static diagnostic => diagnostic.Payload)
            .ToArray();
        var v2CompletionIndex = Array.FindIndex(
            completionPayloads,
            payload => payload is GrainDirectoryEvents.RangeOperationCompleted completion
                && completion.SiloAddress.Equals(receiver.Address)
                && completion.Version == new MembershipVersion(2)
                && completion.Range == receiverRangeV2
                && completion.OperationName == GrainDirectoryEvents.AcquireOperationName);
        var v3CompletionIndex = Array.FindIndex(
            completionPayloads,
            payload => payload is GrainDirectoryEvents.RangeOperationCompleted completion
                && completion.SiloAddress.Equals(receiver.Address)
                && completion.Version == new MembershipVersion(3)
                && completion.Range == transferredRange
                && completion.OperationName == GrainDirectoryEvents.ReleaseOperationName);
        var v4CompletionIndex = Array.FindIndex(
            completionPayloads,
            payload => payload is GrainDirectoryEvents.RangeOperationCompleted completion
                && completion.SiloAddress.Equals(receiver.Address)
                && completion.Version == new MembershipVersion(4)
                && completion.Range == transferredRange
                && completion.OperationName == GrainDirectoryEvents.AcquireOperationName);
        Assert.InRange(v2CompletionIndex, 0, completionPayloads.Length - 1);
        Assert.True(v2CompletionIndex < v3CompletionIndex, fixture.DescribeState("same receiver v2-v3 order"));
        Assert.True(v3CompletionIndex < v4CompletionIndex, fixture.DescribeState("same receiver v3-v4 order"));
        await AssertSingleLocalRegistrationAsync(fixture, grainId, expectedV3, activeV2, cancellationToken);
        AssertAddress(
            expectedV3,
            fixture.Registrations.Lookup(
                grainId,
                activeV2.Select(static node => node.Address).ToHashSet()));
        Assert.Empty(fixture.FatalErrors);
        Assert.Equal(0, fixture.Transport.PendingCount);
    }

    private static async Task<GrainAddress?> RegisterRemotelyAsync(
        ControlledProtocolFixture fixture,
        ControlledNode caller,
        ControlledNode owner,
        GrainAddress proposed,
        GrainAddress? previousAddress,
        MembershipVersion version,
        string phase,
        CancellationToken cancellationToken)
    {
        var operation = caller.Directory.Register(proposed, previousAddress, cancellationToken);
        var envelope = await fixture.Transport.WaitForQueuedAsync(
            candidate => candidate.Source.Equals(caller.Address)
                && candidate.Destination.Equals(owner.Address)
                && candidate.Operation == nameof(IGrainDirectoryPartition.RegisterAsync)
                && candidate.MembershipVersion == version
                && candidate.GrainId == proposed.GrainId,
            $"{phase} queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(envelope.Sequence),
            $"{phase} delivered",
            cancellationToken);
        return await fixture.GuardAsync(operation, $"{phase} response", cancellationToken);
    }

    private static async Task<GrainAddress?> LookupRemotelyAsync(
        ControlledProtocolFixture fixture,
        ControlledNode caller,
        ControlledNode owner,
        GrainId grainId,
        MembershipVersion version,
        string phase,
        CancellationToken cancellationToken)
    {
        var operation = caller.Directory.Lookup(grainId, cancellationToken);
        var envelope = await fixture.Transport.WaitForQueuedAsync(
            candidate => candidate.Source.Equals(caller.Address)
                && candidate.Destination.Equals(owner.Address)
                && candidate.Operation == nameof(IGrainDirectoryPartition.LookupAsync)
                && candidate.MembershipVersion == version
                && candidate.GrainId == grainId,
            $"{phase} queued",
            cancellationToken);
        await fixture.GuardAsync(
            fixture.Transport.DeliverAsync(envelope.Sequence),
            $"{phase} delivered",
            cancellationToken);
        return await fixture.GuardAsync(operation, $"{phase} response", cancellationToken);
    }

    private static async Task<IReadOnlyList<RpcEnvelope>> DeliverRecoveryRequestsAsync(
        ControlledProtocolFixture fixture,
        ControlledNode recoveringNode,
        MembershipVersion version,
        RingRange range,
        int expectedCount,
        string phase,
        CancellationToken cancellationToken)
    {
        var result = new List<RpcEnvelope>(expectedCount);
        for (var index = 0; index < expectedCount; index++)
        {
            var envelope = await fixture.Transport.WaitForQueuedAsync(
                candidate => candidate.Source.Equals(recoveringNode.Address)
                    && candidate.Operation == nameof(IGrainDirectoryClient.RecoverRegisteredActivations)
                    && candidate.MembershipVersion == version
                    && candidate.Range == range,
                $"{phase} recovery request {index + 1}/{expectedCount}",
                cancellationToken);
            await fixture.GuardAsync(
                fixture.Transport.DeliverAsync(envelope.Sequence),
                $"{phase} recovery response {index + 1}/{expectedCount}",
                cancellationToken);
            result.Add(envelope);
        }

        return result;
    }

    private static async Task AssertAtMostOneLocalRegistrationAsync(
        ControlledProtocolFixture fixture,
        GrainId grainId,
        IReadOnlyCollection<ControlledNode> nodes,
        CancellationToken cancellationToken)
    {
        var registrations = new List<(ControlledNode Node, GrainAddress Address)>();
        foreach (var node in nodes)
        {
            if (await node.ProbeLocalAsync(grainId, cancellationToken) is { } address)
            {
                registrations.Add((node, address));
            }
        }

        Assert.True(
            registrations.Count <= 1,
            fixture.DescribeState($"more than one local registration for {grainId}"));
    }

    private static async Task AssertSingleLocalRegistrationAsync(
        ControlledProtocolFixture fixture,
        GrainId grainId,
        GrainAddress expected,
        IReadOnlyCollection<ControlledNode> liveNodes,
        CancellationToken cancellationToken)
    {
        var expectedOwner = fixture.Ownership.GetOwner(
            grainId.GetUniformHashCode(),
            liveNodes.Select(static node => node.Address).ToHashSet());
        var registrations = new List<(ControlledNode Node, GrainAddress Address)>();
        foreach (var node in liveNodes)
        {
            if (await node.ProbeLocalAsync(grainId, cancellationToken) is { } address)
            {
                registrations.Add((node, address));
            }
        }

        var registration = Assert.Single(registrations);
        Assert.Equal(expectedOwner, registration.Node.Address);
        AssertAddress(expected, registration.Address);
    }

    private static void AssertRangeCompletionAbsent(
        ControlledProtocolFixture fixture,
        ControlledNode node,
        MembershipVersion version,
        RingRange range,
        string operationName)
    {
        Assert.DoesNotContain(
            fixture.DirectoryEvents.GetEvents(nameof(GrainDirectoryEvents.RangeOperationCompleted)),
            diagnostic => diagnostic.Payload is GrainDirectoryEvents.RangeOperationCompleted payload
                && payload.SiloAddress.Equals(node.Address)
                && payload.PartitionIndex == 0
                && payload.Version == version
                && payload.Range == range
                && payload.OperationName == operationName);
    }

    public enum CapturedSnapshotOutcome
    {
        DeliverCapturedSnapshot,
        FaultCapturedSnapshotAndRecover,
    }

    private static ControlledProtocolFixture CreateThreeNodeFixture(
        SiloAddress firstReceiver,
        SiloAddress source,
        SiloAddress secondReceiver) => new(
            (secondReceiver, 0x3F00_0000u),
            (firstReceiver, 0x4000_0000u),
            (source, 0x4100_0000u));

    private static GrainId FindGrainIdOwnedBy(
        ControlledProtocolFixture fixture,
        SiloAddress owner,
        string prefix) =>
        FindGrainIdOwnedBy(
            fixture,
            owner,
            prefix,
            fixture.Nodes.Select(static node => node.Address).ToHashSet());

    private static GrainId FindGrainIdOwnedBy(
        ControlledProtocolFixture fixture,
        SiloAddress owner,
        string prefix,
        IReadOnlySet<SiloAddress> activeSilos)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var candidate = GrainId.Create("controlled-directory", $"{prefix}-{index}");
            if (fixture.Ownership.GetOwner(candidate.GetUniformHashCode(), activeSilos).Equals(owner))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a deterministic grain owned by {owner}.");
    }

    private static RingRange ToRingRange(OracleRange range) => RingRange.Create(range.Start, range.End);

    private static void AssertAddress(GrainAddress expected, GrainAddress? actual)
    {
        var value = Assert.IsType<GrainAddress>(actual);
        Assert.Equal(expected.GrainId, value.GrainId);
        Assert.Equal(expected.ActivationId, value.ActivationId);
        Assert.Equal(expected.SiloAddress, value.SiloAddress);
        Assert.Equal(expected.MembershipVersion, value.MembershipVersion);
    }

    private sealed class ControlledProtocolFixture : IAsyncDisposable
    {
        internal const int MaximumActionCount = 256;
        private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(15);
        private readonly object _traceLock = new();
        private long _nextSequence;
        private readonly Dictionary<long, ClusterMembershipSnapshot> _publishedViews = [];
        private readonly Dictionary<SiloAddress, SiloStatus> _lastStatuses = [];
        private bool _started;
        private bool _disposed;

        public ControlledProtocolFixture()
            : this(new GrainDirectoryOptions().EnablePreviousViewRequests)
        {
        }

        public ControlledProtocolFixture(bool enablePreviousViewRequests)
            : this(
                enablePreviousViewRequests,
                TimeSpan.Zero,
                (SiloAddress.FromParsableString("127.0.0.1:11111@101"), 0x4000_0000u),
                (SiloAddress.FromParsableString("127.0.0.1:11112@102"), 0xC000_0000u))
        {
        }

        public ControlledProtocolFixture(params (SiloAddress Silo, uint Boundary)[] boundaries)
            : this(new GrainDirectoryOptions().EnablePreviousViewRequests, TimeSpan.Zero, boundaries)
        {
        }

        public ControlledProtocolFixture(bool enablePreviousViewRequests, TimeSpan rangeLeaseDuration, params (SiloAddress Silo, uint Boundary)[] boundaries)
        {
            EnablePreviousViewRequests = enablePreviousViewRequests;
            RangeLeaseDuration = rangeLeaseDuration;
            TimeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero));
            DirectoryEvents = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
            Ownership = new OwnershipOracle(boundaries);
            Registrations = new AcceptedRegistrationOracle();
            Transport = new ControlledDirectoryTransport(this);

            foreach (var (address, _) in Ownership.Boundaries)
            {
                Nodes.Add(new ControlledNode(this, address));
            }
        }

        public Task<DiagnosticEvent> WaitForRangeOperationAsync(
            ControlledNode node,
            long version,
            RingRange range,
            string operationName,
            bool canceled,
            string phase,
            CancellationToken cancellationToken) =>
            WaitForDirectoryEventAsync(
                nameof(GrainDirectoryEvents.RangeOperationCompleted),
                diagnostic => diagnostic.Payload is GrainDirectoryEvents.RangeOperationCompleted payload
                    && payload.SiloAddress.Equals(node.Address)
                    && payload.PartitionIndex == 0
                    && payload.Version == new MembershipVersion(version)
                    && payload.Range == range
                    && payload.OperationName == operationName
                    && payload.Canceled == canceled,
                phase,
                cancellationToken);

        public Task<DiagnosticEvent> WaitForRangeOperationStartedAsync(
            ControlledNode node,
            long version,
            RingRange range,
            string operationName,
            string phase,
            CancellationToken cancellationToken) =>
            WaitForDirectoryEventAsync(
                nameof(GrainDirectoryEvents.RangeOperationStarted),
                diagnostic => diagnostic.Payload is GrainDirectoryEvents.RangeOperationStarted payload
                    && payload.SiloAddress.Equals(node.Address)
                    && payload.PartitionIndex == 0
                    && payload.Version == new MembershipVersion(version)
                    && payload.Range == range
                    && payload.OperationName == operationName,
                phase,
                cancellationToken);

        public FakeTimeProvider TimeProvider { get; }
        public bool EnablePreviousViewRequests { get; }
        public TimeSpan RangeLeaseDuration { get; }
        public DiagnosticEventCollector DirectoryEvents { get; }
        public ControlledDirectoryTransport Transport { get; }
        public OwnershipOracle Ownership { get; }
        public AcceptedRegistrationOracle Registrations { get; }
        public List<ControlledNode> Nodes { get; } = [];
        public ConcurrentQueue<Exception> FatalErrors { get; } = new();
        public List<TraceEntry> Trace { get; } = [];
        public GrainAddress? ExpectedRecord { get; set; }
        public GrainAddress? ActualRecord { get; set; }

        public ControlledNode GetNode(SiloAddress address) =>
            Assert.Single(Nodes, node => node.Address.Equals(address));

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Assert.False(_started);
            _started = true;
            AppendTrace("lifecycle:start");
            await GuardAsync(
                Task.WhenAll(Nodes.Select(node => node.Lifecycle.OnStart(cancellationToken))),
                "lifecycle start",
                cancellationToken);
        }

        public Task PublishAndObserveAsync(
            long version,
            IReadOnlyCollection<ControlledNode> activeNodes,
            bool assertRangeCompletion,
            CancellationToken cancellationToken) =>
            PublishAndObserveAsync(
                version,
                Nodes,
                activeNodes,
                declaredDeadNodes: [],
                assertRangeCompletion,
                cancellationToken);

        public async Task PublishAndObserveAsync(
            long version,
            IReadOnlyCollection<ControlledNode> observers,
            IReadOnlyCollection<ControlledNode> activeNodes,
            IReadOnlyCollection<ControlledNode> declaredDeadNodes,
            bool assertRangeCompletion,
            CancellationToken cancellationToken,
            IReadOnlyCollection<ControlledNode>? shuttingDownNodes = null,
            IReadOnlyCollection<ControlledNode>? stoppingNodes = null)
        {
            var membershipVersion = new MembershipVersion(version);
            var members = activeNodes
                .Select(static node => new ClusterMember(
                    node.Address,
                    SiloStatus.Active,
                    $"controlled-{node.Address.Endpoint.Port}"))
                .Concat(declaredDeadNodes.Select(static node => new ClusterMember(
                    node.Address,
                    SiloStatus.Dead,
                    $"controlled-{node.Address.Endpoint.Port}",
                    wasDeclaredDead: true)))
                .Concat((shuttingDownNodes ?? []).Select(static node => new ClusterMember(
                    node.Address,
                    SiloStatus.ShuttingDown,
                    $"controlled-{node.Address.Endpoint.Port}")))
                .Concat((stoppingNodes ?? []).Select(static node => new ClusterMember(
                    node.Address,
                    SiloStatus.Stopping,
                    $"controlled-{node.Address.Endpoint.Port}")))
                .ToImmutableDictionary(static member => member.SiloAddress);
            var snapshot = new ClusterMembershipSnapshot(members, membershipVersion);
            if (_publishedViews.TryGetValue(version, out var published))
            {
                Assert.Equal(
                    published.Members.OrderBy(entry => entry.Key)
                        .Select(entry => (entry.Key, entry.Value.Status, entry.Value.WasDeclaredDead)),
                    snapshot.Members.OrderBy(entry => entry.Key)
                        .Select(entry => (entry.Key, entry.Value.Status, entry.Value.WasDeclaredDead)));
            }
            else
            {
                foreach (var (address, status) in _lastStatuses)
                {
                    if (snapshot.Members.TryGetValue(address, out var member)
                        && status is SiloStatus.ShuttingDown or SiloStatus.Stopping or SiloStatus.Dead
                        && member.Status < status)
                    {
                        throw new InvalidOperationException(
                            $"Invalid scenario: silo incarnation {address} regressed from {status} to {member.Status} at v{version}.");
                    }
                }

                foreach (var address in _lastStatuses.Keys.ToArray())
                {
                    if (!snapshot.Members.ContainsKey(address))
                    {
                        _lastStatuses[address] = SiloStatus.Dead;
                    }
                }

                foreach (var (address, member) in snapshot.Members)
                {
                    _lastStatuses[address] = member.Status;
                }

                _publishedViews.Add(version, snapshot);
            }

            var observed = observers.Select(node => WaitForDirectoryEventAsync(
                nameof(GrainDirectoryEvents.MembershipVersionObserved),
                diagnostic => diagnostic.Payload is GrainDirectoryEvents.MembershipVersionObserved payload
                    && payload.SiloAddress.Equals(node.Address)
                    && payload.PartitionIndex == 0
                    && payload.Version == membershipVersion,
                $"membership observed {node.Address} v{version}",
                cancellationToken)).ToArray();

            Task<DiagnosticEvent>[] completed = [];
            if (assertRangeCompletion)
            {
                completed = observers.Select(node =>
                {
                    var expectedRange = Ownership.GetRange(
                        node.Address,
                        activeNodes.Select(static activeNode => activeNode.Address).ToHashSet());
                    return WaitForDirectoryEventAsync(
                        nameof(GrainDirectoryEvents.RangeOperationCompleted),
                        diagnostic => diagnostic.Payload is GrainDirectoryEvents.RangeOperationCompleted payload
                            && payload.SiloAddress.Equals(node.Address)
                            && payload.PartitionIndex == 0
                            && payload.Version == membershipVersion
                            && payload.Range.Start == expectedRange.Start
                            && payload.Range.End == expectedRange.End
                            && payload.OperationName == GrainDirectoryEvents.AcquireOperationName
                            && !payload.Canceled,
                        $"range acquire completed {node.Address} v{version} {expectedRange}",
                        cancellationToken);
                }).ToArray();
            }

            AppendTrace($"membership:publish:v{version}:active={string.Join(",", activeNodes.Select(static node => node.Address))}");
            await Task.WhenAll(observers.Select(node => node.Membership.PublishAsync(snapshot).AsTask()));
            var observedEvents = await GuardAsync(Task.WhenAll(observed), $"membership v{version}", cancellationToken);
            Assert.All(observedEvents, diagnostic =>
            {
                var payload = Assert.IsType<GrainDirectoryEvents.MembershipVersionObserved>(diagnostic.Payload);
                Assert.Equal(0, payload.PartitionIndex);
                Assert.Equal(membershipVersion, payload.Version);
            });

            if (assertRangeCompletion)
            {
                var completedEvents = await GuardAsync(
                    Task.WhenAll(completed),
                    $"range completion v{version}",
                    cancellationToken);
                Assert.All(completedEvents, diagnostic =>
                {
                    var payload = Assert.IsType<GrainDirectoryEvents.RangeOperationCompleted>(diagnostic.Payload);
                    Assert.Equal(GrainDirectoryEvents.AcquireOperationName, payload.OperationName);
                    Assert.False(payload.Canceled);
                });
            }
        }

        public void AdvanceTime(TimeSpan duration)
        {
            AppendTrace($"time:advance:{duration}");
            TimeProvider.Advance(duration);
        }

        public async Task<DiagnosticEvent> WaitForDirectoryEventAsync(
            string eventName,
            Func<DiagnosticEvent, bool> predicate,
            string phase,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await DirectoryEvents.WaitForEventAsync(
                    eventName,
                    predicate,
                    DeadlockGuard,
                    cancellationToken);
                AppendTrace($"diagnostic:{eventName}:{phase}");
                return result;
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(DescribeState(phase), exception);
            }
        }

        public async Task GuardAsync(Task task, string phase, CancellationToken cancellationToken)
        {
            try
            {
                await task.WaitAsync(DeadlockGuard, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(DescribeState(phase), exception);
            }
        }

        public async Task<T> GuardAsync<T>(Task<T> task, string phase, CancellationToken cancellationToken)
        {
            try
            {
                return await task.WaitAsync(DeadlockGuard, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(DescribeState(phase), exception);
            }
        }

        public long AppendTrace(string action)
        {
            const int maximumDiagnosticReplayEntries = 16;
            long sequence;
            TraceEntry[]? diagnosticReplay = null;
            lock (_traceLock)
            {
                sequence = ++_nextSequence;
                if (sequence <= MaximumActionCount)
                {
                    Trace.Add(new(sequence, action));
                }
                else if (!_disposed)
                {
                    diagnosticReplay = [.. Trace.TakeLast(maximumDiagnosticReplayEntries)];
                }
            }

            if (diagnosticReplay is not null)
            {
                var replay = string.Join(
                    " | ",
                    diagnosticReplay.Select(static entry => $"{entry.Sequence}:{entry.Action}"));
                throw new InvalidOperationException(
                    $"Action cap {MaximumActionCount} exceeded by {sequence}:'{action}'; replay=[{replay}]");
            }

            return sequence;
        }

        public string DescribeState(string phase)
        {
            TraceEntry[] trace;
            lock (_traceLock)
            {
                trace = [.. Trace];
            }

            var views = string.Join(
                "; ",
                Nodes.Select(node => $"{node.Address}=cluster:{node.Membership.CurrentSnapshot.Version}/directory:{node.Partition.CurrentView.Version}"));
            var envelopes = string.Join(";", Transport.Envelopes);
            var events = string.Join(
                "; ",
                DirectoryEvents.Events.Select(diagnostic => $"{diagnostic.Name}:{diagnostic.Payload}"));
            var fatals = string.Join("; ", FatalErrors.Select(static error => error.ToString()));
            var replay = string.Join(" | ", trace.Select(static entry => $"{entry.Sequence}:{entry.Action}"));
            return $"seed=controlled-directory-protocol-v1; phase={phase}; views=[{views}]; "
                + $"oracleOwner={Ownership.DescribeOwners()}; envelopes=[{envelopes}]; pending=[{Transport.DescribePending()}]; "
                + $"captured=[{Transport.DescribeCaptured()}]; diagnostics=[{events}]; fatalErrors=[{fatals}]; "
                + $"expected={ExpectedRecord?.ToFullString() ?? "<null>"}; actual={ActualRecord?.ToFullString() ?? "<null>"}; "
                + $"fakeTime={TimeProvider.GetUtcNow():O}; replay=[{replay}]";
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var errors = new List<Exception>();
            Capture(() => Transport.FaultAllPending(new OperationCanceledException("Controlled protocol fixture is stopping.")));

            if (_started)
            {
                foreach (var node in Nodes.AsEnumerable().Reverse())
                {
                    await CaptureAsync($"stop {node.Address}", async () =>
                    {
                        // Cancel snapshot draining during teardown so cleanup
                        // cannot mask the scenario's failed assertion.
                        using var stopCts = new CancellationTokenSource();
                        stopCts.Cancel();

                        try
                        {
                            await node.StopAsync(stopCts.Token).WaitAsync(DeadlockGuard);
                        }
                        catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
                        {
                        }
                    });
                }
            }

            Capture(() => Transport.FaultAllPending(new OperationCanceledException("All controlled nodes have stopped.")));
            Capture(Transport.DisposeProxies);
            foreach (var node in Nodes.AsEnumerable().Reverse())
            {
                await CaptureAsync($"dispose {node.Address}", () => node.DisposeAsync().AsTask());
            }

            Capture(DirectoryEvents.Dispose);
            if (errors.Count > 0)
            {
                throw new AggregateException("Controlled protocol fixture cleanup failed.", errors);
            }

            void Capture(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            async Task CaptureAsync(string phase, Func<Task> action)
            {
                try
                {
                    await action().WaitAsync(DeadlockGuard);
                }
                catch (Exception exception)
                {
                    errors.Add(new InvalidOperationException(DescribeState($"cleanup {phase}"), exception));
                }
            }
        }
    }

    private sealed class ControlledNode : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly DirectoryMembershipService _directoryMembership;

        public ControlledNode(ControlledProtocolFixture fixture, SiloAddress address)
        {
            Fixture = fixture;
            Address = address;
            TimeProvider = fixture.TimeProvider;
            Membership = new ControlledClusterMembershipService(fixture, address);
            GrainFactory = Substitute.For<IInternalGrainFactory>();

            var services = new ServiceCollection()
                .AddMetrics()
                .AddSingleton<OrleansInstruments>()
                .AddSingleton<SchedulerInstruments>()
                .AddSingleton<CatalogInstruments>()
                .AddSingleton<DirectoryInstruments>()
                .AddSingleton<GrainInstruments>()
                .AddSingleton<MessagingInstruments>()
                .AddSingleton<MessagingProcessingInstruments>()
                .AddSingleton(GrainFactory)
                .AddSingleton<GrainDirectoryResolver>(serviceProvider => new(
                    serviceProvider,
                    new GrainPropertiesResolver(Substitute.For<IClusterManifestProvider>()),
                    []))
                .AddGrainDirectory<DistributedGrainDirectory>(
                    GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY,
                    (_, _) => Directory!);
            _services = services.BuildServiceProvider();
            Activations = new(_services.GetRequiredService<CatalogInstruments>());

            GrainFactory.GetSystemTarget<IGrainDirectoryPartition>(Arg.Any<GrainId>())
                .Returns(call =>
                {
                    var target = call.Arg<GrainId>();
                    fixture.AppendTrace($"factory:partition:source={Address}:target={target}");
                    return fixture.Transport.CreatePartitionProxy(Address, target);
                });
            GrainFactory.GetSystemTarget<IGrainDirectoryClient>(Arg.Any<GrainType>(), Arg.Any<SiloAddress>())
                .Returns(call =>
                {
                    var destination = call.ArgAt<SiloAddress>(1);
                    fixture.AppendTrace($"factory:client:source={Address}:target={destination}");
                    return fixture.Transport.CreateDirectoryClientProxy(Address, destination);
                });

            _directoryMembership = new(
                Membership,
                GrainFactory,
                NullLogger<DirectoryMembershipService>.Instance,
                partitionsPerSilo: 1,
                fixture.Ownership.GetBoundaries);

            var siloDetails = Substitute.For<ILocalSiloDetails>();
            siloDetails.SiloAddress.Returns(address);
            Shared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: siloDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                Activations,
                _services.GetRequiredService<SchedulerInstruments>(),
                _services.GetRequiredService<GrainInstruments>(),
                _services.GetRequiredService<MessagingInstruments>(),
                _services.GetRequiredService<MessagingProcessingInstruments>());
            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            fatalErrorHandler
                .When(handler => handler.OnFatalException(
                    Arg.Any<object>(),
                    Arg.Any<string>(),
                    Arg.Any<Exception>()))
                .Do(call => fixture.FatalErrors.Enqueue(call.Arg<Exception>()));
            Directory = new(
                _directoryMembership,
                fatalErrorHandler,
                NullLogger<DistributedGrainDirectory>.Instance,
                _services,
                GrainFactory,
                _services.GetRequiredService<DirectoryInstruments>(),
                Options.Create(new GrainDirectoryOptions
                {
                    PartitionsPerSilo = 1,
                    RangeLeaseDuration = fixture.RangeLeaseDuration,
                    EnablePreviousViewRequests = fixture.EnablePreviousViewRequests
                }),
                Options.Create(new ClusterMembershipOptions()),
                TimeProvider,
                Shared);
            Partition = Assert.IsType<GrainDirectoryPartition>(
                Activations.FindTarget(GrainDirectoryPartition.CreateGrainId(address, 0).GrainId));
            Lifecycle = new(NullLogger<SiloLifecycleSubject>.Instance, siloDetails);
            ((ILifecycleParticipant<ISiloLifecycle>)Directory).Participate(Lifecycle);
        }

        public ControlledProtocolFixture Fixture { get; }
        public SiloAddress Address { get; }
        public TimeProvider TimeProvider { get; }
        public ControlledClusterMembershipService Membership { get; }
        public IInternalGrainFactory GrainFactory { get; }
        public ActivationDirectory Activations { get; }
        public SystemTargetShared Shared { get; }
        public DistributedGrainDirectory Directory { get; }
        public GrainDirectoryPartition Partition { get; }
        public SiloLifecycleSubject Lifecycle { get; }

        public bool LifecycleStopCalled { get; private set; }

        public IGrainContext SeedRecoveryActivation(GrainAddress address)
        {
            var context = Substitute.For<IGrainContext>();
            context.Equals(Arg.Any<IGrainContext>()).Returns(call => ReferenceEquals(context, call.Arg<IGrainContext>()));
            context.GrainId.Returns(address.GrainId);
            context.ActivationId.Returns(address.ActivationId);
            context.Address.Returns(address);
            context.GetComponent(typeof(PlacementStrategy)).Returns(RandomPlacement.Singleton);
            Activations.RecordNewTarget(context);
            Fixture.AppendTrace($"activation:seed:{Address}:{address.GrainId}:{address.ActivationId}");
            return context;
        }

        public async Task<GrainAddress?> ProbeLocalAsync(GrainId grainId, CancellationToken cancellationToken)
        {
            GrainAddress? result = null;
            await Partition.RunOrQueueTask(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Same(Partition, RuntimeContext.Current);
                result = Partition.LookupCore(grainId);
                return Task.CompletedTask;
            });
            return result;
        }

        public async Task WaitForMembershipVersionAsync(
            MembershipVersion version,
            CancellationToken cancellationToken)
        {
            await Partition.RunOrQueueTask(async () =>
            {
                Assert.Same(Partition, RuntimeContext.Current);
                await ((IGrainDirectoryTestHooks)Partition).WaitForMembershipVersionAsync(version, cancellationToken);
            });
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            LifecycleStopCalled = true;
            return Lifecycle.OnStop(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Membership.DisposeAsync();
            await _directoryMembership.DisposeAsync();
            await ((IAsyncDisposable)Activations).DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class ControlledClusterMembershipService : IClusterMembershipService, IAsyncDisposable
    {
        private readonly ControlledProtocolFixture _fixture;
        private readonly SiloAddress _address;
        private readonly Channel<ClusterMembershipSnapshot> _updates =
            Channel.CreateUnbounded<ClusterMembershipSnapshot>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        private readonly object _lock = new();
        private TaskCompletionSource<MembershipVersion> _refreshObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ClusterMembershipSnapshot _current = ClusterMembershipSnapshot.Default;
        private ClusterMembershipSnapshot? _heldPublication;

        public ControlledClusterMembershipService(ControlledProtocolFixture fixture, SiloAddress address)
        {
            _fixture = fixture;
            _address = address;
        }

        public ClusterMembershipSnapshot CurrentSnapshot => Volatile.Read(ref _current);
        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => _updates.Reader.ReadAllAsync();

        public void HoldPublication(ClusterMembershipSnapshot snapshot)
        {
            Assert.Null(_heldPublication);
            Assert.True(snapshot.Version > CurrentSnapshot.Version);
            _heldPublication = snapshot;
            Volatile.Write(ref _current, snapshot);
        }

        public void ReleasePublication()
        {
            var snapshot = Assert.IsType<ClusterMembershipSnapshot>(_heldPublication);
            _heldPublication = null;
            Assert.True(_updates.Writer.TryWrite(snapshot));
        }

        public ValueTask PublishAsync(ClusterMembershipSnapshot snapshot)
        {
            var previous = CurrentSnapshot;
            if (snapshot.Version <= previous.Version)
            {
                throw new InvalidOperationException(
                    $"Membership for {_address} must increase: previous={previous.Version}, proposed={snapshot.Version}.");
            }

            Volatile.Write(ref _current, snapshot);
            if (!_updates.Writer.TryWrite(snapshot))
            {
                throw new InvalidOperationException($"Membership stream for {_address} is closed.");
            }

            _fixture.AppendTrace($"membership:node={_address}:v{snapshot.Version}");
            return ValueTask.CompletedTask;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<MembershipVersion> observed;
            lock (_lock)
            {
                observed = _refreshObserved;
                _refreshObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            observed.TrySetResult(minimumVersion);
            _fixture.AppendTrace($"membership:refresh:node={_address}:minimum={minimumVersion}");
            return ValueTask.CompletedTask;
        }

        public Task<MembershipVersion> WaitForRefreshAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return _refreshObserved.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<bool> TryKill(SiloAddress siloAddress)
        {
            _fixture.AppendTrace($"membership:kill:observer={_address}:target={siloAddress}");
            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync()
        {
            _updates.Writer.TryComplete();
            lock (_lock)
            {
                _refreshObserved.TrySetCanceled();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledDirectoryTransport
    {
        internal const int MaximumEnvelopeCount = 64;
        private readonly ControlledProtocolFixture _fixture;
        private readonly object _lock = new();
        private readonly List<RpcEnvelope> _envelopes = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<SiloAddress> _crashed = [];
        private readonly Dictionary<(SiloAddress Source, GrainId Target), ControlledPartitionProxy> _partitionProxies = [];
        private readonly Dictionary<(SiloAddress Source, SiloAddress Destination), ControlledDirectoryClientProxy> _clientProxies = [];

        public ControlledDirectoryTransport(ControlledProtocolFixture fixture) => _fixture = fixture;

        public IReadOnlyList<RpcEnvelope> Envelopes
        {
            get
            {
                lock (_lock)
                {
                    return [.. _envelopes];
                }
            }
        }

        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _envelopes.Count(static envelope => envelope.State is
                        RpcEnvelopeState.Queued or RpcEnvelopeState.Invoking or RpcEnvelopeState.ResponseCaptured);
                }
            }
        }

        public IGrainDirectoryPartition CreatePartitionProxy(SiloAddress source, GrainId target)
        {
            lock (_lock)
            {
                if (!_partitionProxies.TryGetValue((source, target), out var result))
                {
                    result = new(this, source, GetPartitionNode(target));
                    _partitionProxies.Add((source, target), result);
                }

                return result;
            }
        }

        public IGrainDirectoryClient CreateDirectoryClientProxy(SiloAddress source, SiloAddress destination)
        {
            lock (_lock)
            {
                if (!_clientProxies.TryGetValue((source, destination), out var result))
                {
                    result = new(this, source, _fixture.GetNode(destination));
                    _clientProxies.Add((source, destination), result);
                }

                return result;
            }
        }

        public void DisposeProxies()
        {
            lock (_lock)
            {
                foreach (var proxy in _partitionProxies.Values)
                {
                    proxy.Dispose();
                }

                foreach (var proxy in _clientProxies.Values)
                {
                    proxy.Dispose();
                }

                _partitionProxies.Clear();
                _clientProxies.Clear();
            }
        }

        public async Task<RpcEnvelope> WaitForQueuedAsync(
            Func<RpcEnvelope, bool> predicate,
            string phase,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    var match = _envelopes.FirstOrDefault(
                        envelope => envelope.State == RpcEnvelopeState.Queued && predicate(envelope));
                    if (match is not null)
                    {
                        return match;
                    }

                    changed = _changed.Task;
                }

                await _fixture.GuardAsync(changed, phase, cancellationToken);
            }
        }

        public Task DeliverAsync(long sequence) => DeliverCoreAsync(GetEnvelope(sequence), captureResponse: false);

        public Task CaptureResponseAsync(long sequence) => DeliverCoreAsync(GetEnvelope(sequence), captureResponse: true);

        public void ReleaseCapturedResponse(long sequence)
        {
            var envelope = GetEnvelope(sequence);
            object? response;
            lock (_lock)
            {
                if (envelope.State != RpcEnvelopeState.ResponseCaptured)
                {
                    throw new InvalidOperationException($"Envelope {sequence} is {envelope.State}, not captured.");
                }

                response = envelope.Response;
                envelope.State = RpcEnvelopeState.Delivered;
                SignalChanged();
            }

            envelope.Completion.TrySetResult(response);
            envelope.DisposeCancellationRegistration();
            _fixture.AppendTrace($"rpc:released:{sequence}");
        }

        public async Task<(object? Original, object? Duplicate)> DeliverDuplicateAsync(long sequence)
        {
            var envelope = GetEnvelope(sequence);
            await DeliverCoreAsync(envelope, captureResponse: false);
            var original = envelope.Response;
            _fixture.AppendTrace($"rpc:duplicate:start:{sequence}");
            var duplicate = await envelope.InvokeTarget();
            envelope.State = RpcEnvelopeState.Duplicated;
            _fixture.AppendTrace($"rpc:duplicate:complete:{sequence}");
            return (original, duplicate);
        }

        public Task FaultAsync(long sequence, Exception exception) =>
            SettleExceptionAsync(sequence, RpcEnvelopeState.Faulted, exception);

        public Task DropAsync(long sequence, Exception exception) =>
            SettleExceptionAsync(sequence, RpcEnvelopeState.Dropped, exception);

        public bool IsCrashed(SiloAddress silo)
        {
            lock (_lock)
            {
                return _crashed.Contains(silo);
            }
        }

        public void Crash(SiloAddress silo, bool settlePending = true)
        {
            List<RpcEnvelope> affected = [];
            lock (_lock)
            {
                _crashed.Add(silo);
                if (settlePending)
                {
                    affected = _envelopes.Where(envelope =>
                        envelope.State is RpcEnvelopeState.Queued or RpcEnvelopeState.Invoking
                        && envelope.Operation != nameof(IGrainDirectoryPartition.AcknowledgeSnapshotTransferAsync)
                        && (envelope.Source.Equals(silo) || envelope.Destination.Equals(silo))).ToList();
                }
            }

            foreach (var envelope in affected)
            {
                _ = FaultAsync(envelope.Sequence, new SiloUnavailableException($"Silo {silo} is unavailable."));
            }

            _fixture.AppendTrace($"transport:crash:{silo}:settle={settlePending}");
        }

        private Task SettleExceptionAsync(
            long sequence,
            RpcEnvelopeState state,
            Exception exception)
        {
            var envelope = GetEnvelope(sequence);
            lock (_lock)
            {
                envelope.State = state;
                SignalChanged();
            }

            envelope.Completion.TrySetException(exception);
            envelope.DisposeCancellationRegistration();
            _fixture.AppendTrace($"rpc:{state.ToString().ToLowerInvariant()}:{sequence}:{exception.GetType().Name}");
            return Task.CompletedTask;
        }

        public void FaultAllPending(Exception exception)
        {
            foreach (var envelope in Envelopes.Where(envelope =>
                envelope.State is RpcEnvelopeState.Queued or RpcEnvelopeState.Invoking or RpcEnvelopeState.ResponseCaptured))
            {
                _ = FaultAsync(envelope.Sequence, exception);
            }
        }

        public string DescribePending() => string.Join(
            ";",
            Envelopes.Where(static envelope => envelope.State is
                RpcEnvelopeState.Queued or RpcEnvelopeState.Invoking or RpcEnvelopeState.ResponseCaptured));

        public string DescribeCaptured() => string.Join(
            ";",
            Envelopes.Where(static envelope => envelope.State == RpcEnvelopeState.ResponseCaptured));

        private Task<T> Enqueue<T>(
            SiloAddress source,
            SiloAddress destination,
            string interfaceName,
            string operation,
            MembershipVersion? membershipVersion,
            MembershipVersion? rangeVersion,
            GrainId? grainId,
            RingRange? range,
            CancellationToken cancellationToken,
            Func<Task<T>> invokeTarget)
        {
            RpcEnvelope envelope;
            lock (_lock)
            {
                if (_crashed.Contains(source) || _crashed.Contains(destination))
                {
                    return Task.FromException<T>(new SiloUnavailableException($"Silo {destination} is unavailable."));
                }

                if (_envelopes.Count >= MaximumEnvelopeCount)
                {
                    throw new InvalidOperationException(_fixture.DescribeState("envelope cap exceeded"));
                }

                var sequence = _fixture.AppendTrace(
                    $"rpc:queued:{source}->{destination}:{interfaceName}.{operation}:membership={membershipVersion}:range={rangeVersion}");
                envelope = new(
                    sequence,
                    source,
                    destination,
                    interfaceName,
                    operation,
                    membershipVersion,
                    rangeVersion,
                    grainId,
                    range,
                    cancellationToken,
                    async () => await invokeTarget());
                _envelopes.Add(envelope);
                SignalChanged();
            }

            envelope.RegisterCallerCancellation();
            return AwaitResponse<T>(envelope.Completion.Task);
        }

        private async Task DeliverCoreAsync(RpcEnvelope envelope, bool captureResponse)
        {
            lock (_lock)
            {
                if (envelope.State != RpcEnvelopeState.Queued)
                {
                    throw new InvalidOperationException($"Envelope {envelope.Sequence} cannot be delivered from {envelope.State}.");
                }

                envelope.State = RpcEnvelopeState.Invoking;
                SignalChanged();
            }

            _fixture.AppendTrace($"rpc:invoke:{envelope.Sequence}");
            try
            {
                var response = await envelope.InvokeTarget();
                lock (_lock)
                {
                    envelope.Response = response;
                    envelope.State = captureResponse ? RpcEnvelopeState.ResponseCaptured : RpcEnvelopeState.Delivered;
                    SignalChanged();
                }

                if (!captureResponse)
                {
                    if (envelope.Completion.TrySetResult(response))
                    {
                        envelope.RecordCallerCompletion();
                    }

                    envelope.DisposeCancellationRegistration();
                }

                _fixture.AppendTrace(
                    captureResponse ? $"rpc:captured:{envelope.Sequence}" : $"rpc:delivered:{envelope.Sequence}");
            }
            catch (Exception exception)
            {
                lock (_lock)
                {
                    envelope.State = RpcEnvelopeState.Faulted;
                    SignalChanged();
                }

                envelope.Completion.TrySetException(exception);
                envelope.DisposeCancellationRegistration();
                _fixture.AppendTrace($"rpc:target-fault:{envelope.Sequence}:{exception.GetType().Name}");
                throw;
            }
        }

        private RpcEnvelope GetEnvelope(long sequence)
        {
            lock (_lock)
            {
                return Assert.Single(_envelopes, envelope => envelope.Sequence == sequence);
            }
        }

        private void SignalChanged()
        {
            var previous = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        private static async Task<T> AwaitResponse<T>(Task<object?> response) =>
            (T)(await response)!;

        private ControlledNode GetPartitionNode(GrainId target) =>
            Assert.Single(_fixture.Nodes, node => node.Partition.GrainId.Equals(target));

        private sealed class ControlledPartitionProxy(
            ControlledDirectoryTransport transport,
            SiloAddress source,
            ControlledNode destination)
            : SystemTarget(GrainDirectoryPartition.CreateGrainId(destination.Address, 0), destination.Shared),
                IGrainDirectoryPartition
        {
            public ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(
                MembershipVersion version,
                GrainAddress address,
                GrainAddress? currentRegistration,
                CancellationToken cancellationToken = default,
                bool allowPreviousVersion = false)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryPartition),
                    nameof(RegisterAsync),
                    version,
                    rangeVersion: null,
                    address.GrainId,
                    range: null,
                    cancellationToken,
                    () => InvokeOnPartition(
                        destination,
                        () => ((IGrainDirectoryPartition)destination.Partition).RegisterAsync(
                            version,
                            address,
                            currentRegistration,
                            cancellationToken,
                            allowPreviousVersion).AsTask())));
            }

            public ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(
                MembershipVersion version,
                GrainId grainId,
                CancellationToken cancellationToken = default,
                bool allowPreviousVersion = false)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryPartition),
                    nameof(LookupAsync),
                    version,
                    rangeVersion: null,
                    grainId,
                    range: null,
                    cancellationToken,
                    () => InvokeOnPartition(
                        destination,
                        () => ((IGrainDirectoryPartition)destination.Partition).LookupAsync(
                            version,
                            grainId,
                            cancellationToken,
                            allowPreviousVersion).AsTask())));
            }

            public ValueTask<DirectoryResult<bool>> DeregisterAsync(
                MembershipVersion version,
                GrainAddress address,
                CancellationToken cancellationToken = default,
                bool allowPreviousVersion = false)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryPartition),
                    nameof(DeregisterAsync),
                    version,
                    rangeVersion: null,
                    address.GrainId,
                    range: null,
                    cancellationToken,
                    () => InvokeOnPartition(
                        destination,
                        () => ((IGrainDirectoryPartition)destination.Partition).DeregisterAsync(
                            version,
                            address,
                            cancellationToken,
                            allowPreviousVersion).AsTask())));
            }

            public ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(
                MembershipVersion version,
                MembershipVersion rangeVersion,
                RingRange range,
                CancellationToken cancellationToken = default)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryPartition),
                    nameof(GetSnapshotAsync),
                    version,
                    rangeVersion,
                    grainId: null,
                    range,
                    cancellationToken,
                    () => InvokeOnPartition(
                        destination,
                        () => ((IGrainDirectoryPartition)destination.Partition).GetSnapshotAsync(
                            version,
                            rangeVersion,
                            range,
                            cancellationToken).AsTask())));
            }

            public ValueTask<bool> AcknowledgeSnapshotTransferAsync(
                SiloAddress silo,
                int partitionIndex,
                MembershipVersion version,
                CancellationToken cancellationToken = default)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryPartition),
                    nameof(AcknowledgeSnapshotTransferAsync),
                    version,
                    rangeVersion: version,
                    grainId: null,
                    range: null,
                    cancellationToken,
                    () => InvokeOnPartition(
                        destination,
                        () => ((IGrainDirectoryPartition)destination.Partition).AcknowledgeSnapshotTransferAsync(
                            silo,
                            partitionIndex,
                            version,
                            cancellationToken).AsTask())));
            }

            private static async Task<T> InvokeOnPartition<T>(ControlledNode destination, Func<Task<T>> action)
            {
                T result = default!;
                await destination.Partition.RunOrQueueTask(async () =>
                {
                    Assert.Same(destination.Partition, RuntimeContext.Current);
                    result = await action();
                });
                return result;
            }
        }

        private sealed class ControlledDirectoryClientProxy(
            ControlledDirectoryTransport transport,
            SiloAddress source,
            ControlledNode destination)
            : SystemTarget(Constants.GrainDirectoryType, destination.Shared), IGrainDirectoryClient
        {
            public ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(
                MembershipVersion membershipVersion,
                RingRange range,
                bool isValidation,
                CancellationToken cancellationToken = default)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryClient),
                    nameof(GetRegisteredActivations),
                    membershipVersion,
                    rangeVersion: null,
                    grainId: null,
                    range,
                    cancellationToken,
                    () => InvokeOnDirectory(
                        destination,
                        () => ((IGrainDirectoryClient)destination.Directory).GetRegisteredActivations(
                            membershipVersion,
                            range,
                            isValidation,
                            cancellationToken).AsTask())));
            }

            public ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(
                MembershipVersion membershipVersion,
                RingRange range,
                SiloAddress siloAddress,
                int partitionId,
                CancellationToken cancellationToken = default)
            {
                return new(transport.Enqueue(
                    source,
                    destination.Address,
                    nameof(IGrainDirectoryClient),
                    nameof(RecoverRegisteredActivations),
                    membershipVersion,
                    rangeVersion: null,
                    grainId: null,
                    range,
                    cancellationToken,
                    () => InvokeOnDirectory(
                        destination,
                        () => ((IGrainDirectoryClient)destination.Directory).RecoverRegisteredActivations(
                            membershipVersion,
                            range,
                            siloAddress,
                            partitionId,
                            cancellationToken).AsTask())));
            }

            private static async Task<T> InvokeOnDirectory<T>(ControlledNode destination, Func<Task<T>> action)
            {
                T result = default!;
                await destination.Directory.RunOrQueueTask(async () =>
                {
                    Assert.Same(destination.Directory, RuntimeContext.Current);
                    result = await action();
                });
                return result;
            }
        }
    }

    private sealed class RpcEnvelope
    {
        private readonly CancellationToken _callerCancellation;
        private CancellationTokenRegistration _cancellationRegistration;
        private int _invocationCount;
        private int _callerCompletionCount;

        public RpcEnvelope(
            long sequence,
            SiloAddress source,
            SiloAddress destination,
            string interfaceName,
            string operation,
            MembershipVersion? membershipVersion,
            MembershipVersion? rangeVersion,
            GrainId? grainId,
            RingRange? range,
            CancellationToken callerCancellation,
            Func<Task<object?>> invokeTarget)
        {
            Sequence = sequence;
            Source = source;
            Destination = destination;
            Interface = interfaceName;
            Operation = operation;
            MembershipVersion = membershipVersion;
            RangeVersion = rangeVersion;
            GrainId = grainId;
            Range = range;
            _callerCancellation = callerCancellation;
            InvokeTarget = async () =>
            {
                Interlocked.Increment(ref _invocationCount);
                var result = await invokeTarget();
                TargetRanOnDestinationScheduler = true;
                return result;
            };
        }

        public long Sequence { get; }
        public SiloAddress Source { get; }
        public SiloAddress Destination { get; }
        public string Interface { get; }
        public string Operation { get; }
        public MembershipVersion? MembershipVersion { get; }
        public MembershipVersion? RangeVersion { get; }
        public GrainId? GrainId { get; }
        public RingRange? Range { get; }
        public RpcEnvelopeState State { get; set; } = RpcEnvelopeState.Queued;
        public bool TargetRanOnDestinationScheduler { get; private set; }
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int CallerCompletionCount => Volatile.Read(ref _callerCompletionCount);
        public object? Response { get; set; }
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task<object?>> InvokeTarget { get; }

        public void RegisterCallerCancellation()
        {
            if (_callerCancellation.CanBeCanceled)
            {
                _cancellationRegistration = _callerCancellation.Register(
                    static state =>
                    {
                        var envelope = (RpcEnvelope)state!;
                        envelope.Completion.TrySetCanceled(envelope._callerCancellation);
                    },
                    this);
            }
        }

        public void DisposeCancellationRegistration() => _cancellationRegistration.Dispose();

        public void RecordCallerCompletion() => Interlocked.Increment(ref _callerCompletionCount);

        public override string ToString() =>
            $"#{Sequence}:{Source}->{Destination}:{Interface}.{Operation}:{State}";
    }

    private enum RpcEnvelopeState
    {
        Queued,
        Invoking,
        ResponseCaptured,
        Delivered,
        Faulted,
        Dropped,
        Duplicated,
    }

    private sealed class OwnershipOracle
    {
        private readonly (SiloAddress Silo, uint Boundary)[] _boundaries;

        public OwnershipOracle(params (SiloAddress Silo, uint Boundary)[] boundaries)
        {
            _boundaries = [.. boundaries.OrderBy(static entry => entry.Boundary)];
        }

        public IReadOnlyList<(SiloAddress Silo, uint Boundary)> Boundaries => _boundaries;

        public uint[] GetBoundaries(SiloAddress silo, int count)
        {
            Assert.Equal(1, count);
            return [Assert.Single(_boundaries, entry => entry.Silo.Equals(silo)).Boundary];
        }

        public SiloAddress GetOwner(uint hash) =>
            GetOwner(hash, _boundaries.Select(static entry => entry.Silo).ToHashSet());

        public SiloAddress GetOwner(uint hash, IReadOnlySet<SiloAddress> activeSilos)
        {
            var activeBoundaries = _boundaries.Where(entry => activeSilos.Contains(entry.Silo)).ToArray();
            if (activeBoundaries.Length == 1)
            {
                return activeBoundaries[0].Silo;
            }

            for (var index = 0; index < activeBoundaries.Length; index++)
            {
                var current = activeBoundaries[index];
                var end = activeBoundaries[(index + 1) % activeBoundaries.Length].Boundary;
                if (Contains(current.Boundary, end, hash))
                {
                    return current.Silo;
                }
            }

            throw new InvalidOperationException($"No independent owner found for hash 0x{hash:X8}.");
        }

        public OracleRange GetRange(SiloAddress silo) =>
            GetRange(silo, _boundaries.Select(static entry => entry.Silo).ToHashSet());

        public OracleRange GetRange(SiloAddress silo, IReadOnlySet<SiloAddress> activeSilos)
        {
            var activeBoundaries = _boundaries.Where(entry => activeSilos.Contains(entry.Silo)).ToArray();
            var index = Array.FindIndex(activeBoundaries, entry => entry.Silo.Equals(silo));
            Assert.InRange(index, 0, activeBoundaries.Length - 1);
            return activeBoundaries.Length == 1
                ? new(0, 0)
                : new(activeBoundaries[index].Boundary, activeBoundaries[(index + 1) % activeBoundaries.Length].Boundary);
        }

        public string DescribeOwners() => string.Join(
            ",",
            _boundaries.Select(entry => $"{entry.Silo}@0x{entry.Boundary:X8}"));

        private static bool Contains(uint start, uint end, uint value) =>
            start < end ? value > start && value <= end : value > start || value <= end;
    }

    private sealed class AcceptedRegistrationOracle
    {
        private readonly Dictionary<GrainId, GrainAddress> _accepted = [];

        public GrainAddress Register(
            GrainAddress proposed,
            GrainAddress? previousAddress,
            MembershipVersion version,
            IReadOnlySet<SiloAddress> liveSilos)
        {
            if (!_accepted.TryGetValue(proposed.GrainId, out var existing)
                || existing.SiloAddress is null
                || !liveSilos.Contains(existing.SiloAddress)
                || SameIdentity(existing, previousAddress))
            {
                existing = CopyAtVersion(proposed, version);
                _accepted[proposed.GrainId] = existing;
            }

            return existing;
        }

        public GrainAddress? Lookup(GrainId grainId, IReadOnlySet<SiloAddress> liveSilos) =>
            _accepted.TryGetValue(grainId, out var address)
                && address.SiloAddress is not null
                && liveSilos.Contains(address.SiloAddress)
                    ? address
                    : null;

        public void Recover(GrainAddress recovered, IReadOnlySet<SiloAddress> liveSilos)
        {
            if (recovered.SiloAddress is null || !liveSilos.Contains(recovered.SiloAddress))
            {
                return;
            }

            if (!_accepted.TryGetValue(recovered.GrainId, out var existing)
                || recovered.MembershipVersion >= existing.MembershipVersion)
            {
                _accepted[recovered.GrainId] = CopyAtVersion(recovered, recovered.MembershipVersion);
            }
        }

        private static bool SameIdentity(GrainAddress address, GrainAddress? other) =>
            other is not null
            && address.GrainId == other.GrainId
            && address.ActivationId == other.ActivationId
            && Equals(address.SiloAddress, other.SiloAddress);

        private static GrainAddress CopyAtVersion(GrainAddress address, MembershipVersion version) => new()
        {
            GrainId = address.GrainId,
            ActivationId = address.ActivationId,
            SiloAddress = address.SiloAddress,
            MembershipVersion = version,
        };
    }

    private readonly record struct OracleRange(uint Start, uint End)
    {
        public override string ToString() => $"(0x{Start:X8},0x{End:X8}]";
    }

    private readonly record struct TraceEntry(long Sequence, string Action);
}

internal static class ControlledProtocolEnumerableExtensions
{
    public static IEnumerable<(T First, T Second)> Pairwise<T>(this IEnumerable<T> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            yield break;
        }

        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            yield return (previous, enumerator.Current);
            previous = enumerator.Current;
        }
    }

}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ControlledGrainDirectoryProtocolCollection
{
    public const string Name = nameof(ControlledGrainDirectoryProtocolCollection);
}
