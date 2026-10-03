using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxLifecycleBehaviorTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task Deactivation_WaitsForActiveInboxTimerBeforeDisposingShutdownSource()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var shutdown = GetShutdownSource(context);
        var token = shutdown.Token;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/teardown-active");
        using var envelope = CreateEnvelope(receiver, NewMessage(95, "active-timer"), "messages/teardown-active");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var started = Assert.Single(events.Events.Select(static item => item.Payload)
            .OfType<GrainTimerEvents.TickStart>(), item => ReferenceEquals(item.GrainContext, context));
        var tickStopped = events.WaitForEventAsync(
            nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, started.Timer),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        var stoppedAtCancellation = false;
        var cancellations = 0;
        using var registration = token.Register(() =>
        {
            stoppedAtCancellation = events.Events.Any(item =>
                item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, started.Timer));
            Interlocked.Increment(ref cancellations);
        });

        context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Verify active inbox timer teardown."), TestContext.Current.CancellationToken);

        Assert.False(context.Deactivated.IsCompleted);
        Assert.False(tickStopped.IsCompleted);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(token, shutdown.Token);
        handler.Release();
        var stopped = Assert.IsType<GrainTimerEvents.TickStop>((await tickStopped).Payload);
        await WaitForDeactivationAsync(context);

        Assert.Null(stopped.Exception);
        Assert.True(stoppedAtCancellation);
        Assert.Equal(1, cancellations);
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task Deactivation_DisposesQueuedInboxTimerBeforeScopeTeardownAndRecoveryDrainsOnce()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var shutdown = GetShutdownSource(context);
        var token = shutdown.Token;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName, GrainLifecycleEvents.ListenerName);
        using var envelope = CreateEnvelope(receiver, NewMessage(96, "queued-timer"));

        Assert.Equal(DeliveryStatus.Accepted, (await receiver.AcceptAndDeactivateAsync(envelope.Value)).Status);
        await WaitForDeactivationAsync(context);

        var captured = events.Events.Select(static item => item.Payload).ToList();
        var created = Assert.Single(captured.OfType<GrainTimerEvents.Created>(),
            item => ReferenceEquals(item.GrainContext, context));
        Assert.DoesNotContain(captured.OfType<GrainTimerEvents.TickStart>(), item => ReferenceEquals(item.GrainContext, context));
        var disposed = Assert.Single(captured.OfType<GrainTimerEvents.Disposed>(),
            item => ReferenceEquals(item.Timer, created.Timer));
        var deactivated = Assert.Single(captured.OfType<GrainLifecycleEvents.Deactivated>(),
            item => ReferenceEquals(item.GrainContext, context));
        Assert.True(captured.IndexOf(disposed) < captured.IndexOf(deactivated));
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(0, recovered.InboxCount);
    }

    private static CancellationTokenSource GetShutdownSource(IGrainContext context)
    {
        var extensionType = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        var extension = context.ActivationServices.GetRequiredService(extensionType);
        return (CancellationTokenSource)extensionType.GetField("_shutdownCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(extension)!;
    }

    private static async Task WaitForDeactivationAsync(IGrainContext context)
    {
        try
        {
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Inbox activation {context.GrainId} did not complete timer/lifecycle teardown.", exception);
        }
    }
}
