namespace Documentation.Diagnostics.DurableTasks;

public sealed class ContractGrain : DurableGrain
{
    public static readonly DurableSignal<Decision> Reviewed = new("reviewed");

#pragma warning disable ORLEANS0114
    // <orleans0114_violation>
    public async DurableTask<bool> NegotiateAsync(int rounds)
    {
        for (var round = 1; round <= rounds; round++)
        {
            // Every iteration returns the first review, so one rejection rejects every round at once.
            var review = await Reviewed.WaitAsync();
            if (review.Approved)
            {
                return true;
            }
        }

        return false;
    }
    // </orleans0114_violation>
#pragma warning restore ORLEANS0114

    // <orleans0114_fix>
    public async DurableTask<bool> NegotiateEachRoundAsync(int rounds)
    {
        for (var round = 1; round <= rounds; round++)
        {
            // Each round waits for its own signal name: "reviewed/round-1", "reviewed/round-2", and so on.
            var review = await Reviewed.For($"round-{round}").WaitAsync();
            if (review.Approved)
            {
                return true;
            }
        }

        return false;
    }
    // </orleans0114_fix>
}
