using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Timers;

namespace Tests;

// <mocked_grain_tests>
public sealed class ShoppingCartGrainTests
{
    [Fact]
    public async Task AddItemPersistsStateAndNotifiesAuditGrain()
    {
        var context = CreateContext("customer-42");
        var state = Substitute.For<IPersistentState<ShoppingCartState>>();
        state.State.Returns(new ShoppingCartState());
        var audit = Substitute.For<IAuditGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAuditGrain>("customer-42").Returns(audit);
        var grain = new ShoppingCartGrain(
            grainFactory,
            state,
            Substitute.For<ITimerRegistry>(),
            Substitute.For<IReminderRegistry>(),
            context);

        await grain.AddItem("coffee");

        Assert.Equal(["coffee"], state.State.Items);
        await state.Received(1).WriteStateAsync();
        await audit.Received(1).RecordItemAdded("coffee");
    }

    [Fact]
    public async Task ActivationRegistersTimerAndReminder()
    {
        var context = CreateContext("customer-42");
        var state = Substitute.For<IPersistentState<ShoppingCartState>>();
        state.State.Returns(new ShoppingCartState());
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var reminderRegistry = Substitute.For<IReminderRegistry>();
        Func<ShoppingCartGrain, CancellationToken, Task>? timerCallback = null;
        timerRegistry.RegisterGrainTimer(
                context,
                Arg.Do<Func<ShoppingCartGrain, CancellationToken, Task>>(
                    callback => timerCallback = callback),
                Arg.Any<ShoppingCartGrain>(),
                Arg.Is<GrainTimerCreationOptions>(
                    options => options.DueTime == TimeSpan.FromMinutes(1)
                        && options.Period == TimeSpan.FromMinutes(1)))
            .Returns(Substitute.For<IGrainTimer>());
        reminderRegistry.RegisterOrUpdateReminder(
                context.GrainId,
                "cart-checkout",
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(1))
            .Returns(Substitute.For<IGrainReminder>());
        var grain = new ShoppingCartGrain(
            Substitute.For<IGrainFactory>(),
            state,
            timerRegistry,
            reminderRegistry,
            context);

        await grain.OnActivateAsync(CancellationToken.None);
        await timerCallback!(grain, CancellationToken.None);

        await state.Received(1).WriteStateAsync();
        _ = await reminderRegistry.Received(1).RegisterOrUpdateReminder(
            context.GrainId,
            "cart-checkout",
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));
    }

    private static IGrainContext CreateContext(string key)
    {
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("shopping-cart", key));
        return context;
    }
}
// </mocked_grain_tests>
