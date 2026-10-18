namespace Documentation.Diagnostics.DurableTasks;

public sealed class BillingGrain : DurableGrain
{
#pragma warning disable ORLEANS0120
    // <orleans0120_violation>
    public async DurableTask<decimal> BillAsync(string[] items, bool express)
    {
        if (express)
        {
            // Takes $child-1 when the order is express.
            await Reserve("courier");
        }

        var total = 0m;
        foreach (var item in items)
        {
            // Takes $child-1, $child-2, and so on, or $child-2, $child-3, and so on, depending on the branch above.
            total += await Price(item);
        }

        // Takes the identifier after the last price, so a change to the items or the branch shifts it.
        await Charge(total);
        return total;
    }
    // </orleans0120_violation>
#pragma warning restore ORLEANS0120

    // <orleans0120_fix>
    public async DurableTask<decimal> BillByNameAsync(string[] items, bool express)
    {
        if (express)
        {
            await Reserve("courier").WithId("courier");
        }

        var total = 0m;
        foreach (var item in items)
        {
            // Takes price, price$2, and so on, whichever branch ran before it.
            total += await Price(item).WithId("price");
        }

        await Charge(total).WithId("charge");
        return total;
    }
    // </orleans0120_fix>

    private static async DurableTask<bool> Reserve(string carrier) => true;

    private static async DurableTask<decimal> Price(string item) => item.Length;

    private static async DurableTask Charge(decimal total)
    {
    }
}
