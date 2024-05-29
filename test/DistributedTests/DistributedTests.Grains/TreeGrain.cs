using DistributedTests.GrainInterfaces;

namespace DistributedTests.Grains;

public class TreeGrain : Grain, ITreeGrain
{
    // 16^4 grains (~65K)
    public const int FanOutFactor = 16;
    public const int MaxLevel = 4;
    private readonly List<ITreeGrain> _children;

    public TreeGrain()
    {
        var id = this.GetPrimaryKeyLong(out var forestName);

        var level = id == 0 ? 0 : (int)Math.Log(id, FanOutFactor);
        var numChildren = level < MaxLevel ? FanOutFactor : 0;
        _children = new List<ITreeGrain>(numChildren);
        var childBase = (id + 1) * FanOutFactor;
        for (var i = 1; i <= numChildren; i++)
        {
            var child = GrainFactory.GetGrain<ITreeGrain>(childBase + i, keyExtension: forestName);
            _children.Add(child);
        }
    }

    public async ValueTask Ping()
    {
        var tasks = new List<ValueTask>(_children.Count);
        foreach (var child in _children)
        {
            tasks.Add(child.Ping());
        }

        // Wait for the tasks to complete.
        foreach (var task in tasks)
        {
            await task;
        }
    }
}
