namespace Documentation.Diagnostics.DurableTasks;

public sealed class StockGrain : DurableGrain
{
#pragma warning disable ORLEANS0110
    // <orleans0110_violation>
    public async DurableTask<bool> ReserveAllAsync(int[] productIds)
    {
        // Each lambda schedules its child in input order, with a separate journal write for each invocation.
        var reserved = await Task.WhenAll(productIds.Select(async id => await Reserve(id).WithId($"reserve-{id}")));
        return reserved.All(r => r);
    }
    // </orleans0110_violation>
#pragma warning restore ORLEANS0110

    // <orleans0110_fix_when_all>
    public async DurableTask<bool> ReserveEveryLineAsync(int[] productIds)
    {
        // The inputs are reserve/0, reserve/1, and so on, in input order, and their invocations commit in one write.
        var reserved = await DurableTask.WhenAll(productIds.Select(Reserve)).WithId("reserve");
        return reserved.All(r => r);
    }
    // </orleans0110_fix_when_all>

    // <orleans0110_fix_race>
    public async DurableTask<int> ReserveFromFirstWarehouseAsync(int[] warehouses)
    {
        // Instead of Task.WhenAny: the first warehouse to answer wins, and the others are canceled and awaited.
        var (index, _) = await DurableTask.Race(warehouses.Select(Reserve)).WithId("first-warehouse");
        return warehouses[index];
    }
    // </orleans0110_fix_race>

    private static async DurableTask<bool> Reserve(int id) => id > 0;
}
