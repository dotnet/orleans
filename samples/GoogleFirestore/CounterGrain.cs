using Orleans.Runtime;

namespace GoogleFirestore;

public sealed class CounterGrain(
    [PersistentState("counter", "firestore")] IPersistentState<CounterState> state)
    : Grain, ICounterGrain, IRemindable
{
    public async Task<int> Increment()
    {
        state.State.Value++;
        state.State.LastUpdatedUtc = DateTime.UtcNow;
        await state.WriteStateAsync();
        return state.State.Value;
    }

    public async Task EnsureReminder()
    {
        _ = await this.RegisterOrUpdateReminder(
            "heartbeat",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        state.State.ReminderTicks++;
        state.State.LastUpdatedUtc = DateTime.UtcNow;
        await state.WriteStateAsync();
    }
}
