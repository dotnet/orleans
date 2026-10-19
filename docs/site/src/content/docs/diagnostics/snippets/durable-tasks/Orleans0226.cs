namespace Documentation.Diagnostics.DurableTasks;

public sealed class ConversionGrain : DurableGrain
{
    private readonly IExchangeRates _rates;
    private readonly IDurableValue<decimal> _lastRate;

    public ConversionGrain(IExchangeRates rates)
    {
        _rates = rates;
        _lastRate = StateManager.GetOrAddValue<decimal>("last-rate");
    }

#pragma warning disable ORLEANS0226
    // <orleans0226_violation>
    public async DurableTask<decimal> ConvertAsync(string currency, decimal amount)
    {
        await Approve(amount).WithId("approve");

        // The continuation runs on the thread pool: the state change races with the grain's other turns, and commits
        // apart from the result.
        var rate = await _rates.GetRateAsync(currency, DurableTask.Current.CancellationToken).ConfigureAwait(false);
        _lastRate.Value = rate;
        return amount * rate;
    }
    // </orleans0226_violation>
#pragma warning restore ORLEANS0226

    // <orleans0226_fix>
    public async DurableTask<decimal> ConvertOnSchedulerAsync(string currency, decimal amount)
    {
        await Approve(amount).WithId("approve");

        // The continuation returns to the activation's scheduler, and the state change commits with the result.
        var rate = await _rates.GetRateAsync(currency, DurableTask.Current.CancellationToken);
        _lastRate.Value = rate;
        return amount * rate;
    }
    // </orleans0226_fix>

    private static async DurableTask<bool> Approve(decimal amount) => amount > 0;
}
