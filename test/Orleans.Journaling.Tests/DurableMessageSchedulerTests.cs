using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT"), TestCategory("Journaling")]
public class DurableMessageSchedulerTests(IntegrationTestFixture fixture) : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task ScheduledMessage_ReactivatesGrainAndDelivers()
    {
        var grain = fixture.Client.GetGrain<IScheduledMessageTestGrain>(Guid.NewGuid());

        await grain.ScheduleToSelf(TimeSpan.FromMilliseconds(250));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (await grain.GetDeliveryCount() == 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The scheduled message was not delivered.");
    }
}

public interface IScheduledMessageTestGrain : IGrainWithGuidKey
{
    Task ScheduleToSelf(TimeSpan delay);
    Task<int> GetDeliveryCount();
}

public sealed class ScheduledMessageTestGrain(
    IDurableInbox inbox,
    IDurableMessageScheduler scheduler,
    SerializerSessionPool sessionPool) : DurableGrain, IScheduledMessageTestGrain
{
    private int _deliveryCount;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.RegisterHandler(new DeliveryHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task ScheduleToSelf(TimeSpan delay)
    {
        var envelope = new DurableEnvelopeBuilder(sessionPool, this.GetGrainId())
            .To(this.GetGrainId(), "scheduled/test")
            .WithBody("deliver")
            .Build();
        await scheduler.ScheduleAsync(envelope, DateTimeOffset.UtcNow + delay);
        DeactivateOnIdle();
    }

    public Task<int> GetDeliveryCount() => Task.FromResult(_deliveryCount);

    private sealed class DeliveryHandler(ScheduledMessageTestGrain grain) : RouteKeyHandler("scheduled/test")
    {
        protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            grain._deliveryCount++;
            return ValueTask.CompletedTask;
        }
    }
}
