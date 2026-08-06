using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class CollectionExpressionGrain : Grain, ICollectionExpressionGrain
{
    public Task<IEnumerable<int>> GetEnumerable()
        => Task.FromResult<IEnumerable<int>>([1, 2, 3]);

    public Task<IReadOnlyList<int>> GetReadOnlyList()
        => Task.FromResult<IReadOnlyList<int>>([10, 20, 30]);

    public Task<IList<int>> GetList()
        => Task.FromResult<IList<int>>([100, 200, 300]);

    public Task<IReadOnlyCollection<int>> GetReadOnlyCollection()
        => Task.FromResult<IReadOnlyCollection<int>>([4, 5, 6]);

    public Task<ICollection<int>> GetCollection()
        => Task.FromResult<ICollection<int>>([40, 50, 60]);

    public Task<ISet<int>> GetSet()
    {
        HashSet<int> result = [7, 8, 9];
        return Task.FromResult<ISet<int>>(result);
    }

    public Task<IReadOnlySet<int>> GetReadOnlySet()
    {
        HashSet<int> result = [70, 80, 90];
        return Task.FromResult<IReadOnlySet<int>>(result);
    }

    public Task<IDictionary<string, int>> GetDictionary()
        => Task.FromResult<IDictionary<string, int>>(new Dictionary<string, int>
        {
            ["alpha"] = 1,
            ["beta"] = 2,
            ["gamma"] = 3
        });

    public Task<IReadOnlyDictionary<string, int>> GetReadOnlyDictionary()
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>
        {
            ["x"] = 10,
            ["y"] = 20,
            ["z"] = 30
        });
}
