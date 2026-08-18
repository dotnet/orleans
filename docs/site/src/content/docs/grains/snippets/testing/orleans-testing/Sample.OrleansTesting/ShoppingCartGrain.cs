using Orleans;
using Orleans.Runtime;
using Orleans.Timers;

namespace Tests;

// <mockable_grain>
public interface IShoppingCartGrain : IGrainWithStringKey
{
    Task AddItem(string item);
}

public interface IAuditGrain : IGrainWithStringKey
{
    Task RecordItemAdded(string item);
}

[GenerateSerializer]
public sealed class ShoppingCartState
{
    [Id(0)]
    public List<string> Items { get; set; } = [];
}

public sealed class ShoppingCartGrain(
    IGrainFactory grainFactory,
    [PersistentState("cart")] IPersistentState<ShoppingCartState> state,
    ITimerRegistry timerRegistry,
    IReminderRegistry reminderRegistry,
    IGrainContext grainContext) : IShoppingCartGrain, IGrainBase
{
    public IGrainContext GrainContext { get; } = grainContext;

    public async Task AddItem(string item)
    {
        state.State.Items.Add(item);
        await state.WriteStateAsync();

        var audit = grainFactory.GetGrain<IAuditGrain>(this.GetPrimaryKeyString());
        await audit.RecordItemAdded(item);
    }

    public async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        timerRegistry.RegisterGrainTimer(
            GrainContext,
            static (grain, _) => grain.FlushStateAsync(),
            this,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });

        _ = await reminderRegistry.RegisterOrUpdateReminder(
            GrainContext.GrainId,
            reminderName: "cart-checkout",
            dueTime: TimeSpan.FromHours(1),
            period: TimeSpan.FromHours(1));
    }

    private Task FlushStateAsync() => state.WriteStateAsync();
}
// </mockable_grain>
