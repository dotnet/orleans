using Orleans;

namespace MyNs;

[Alias("ITestGrain")]
public interface ITestGrain : IGrainWithStringKey
{
    [Alias("Void")]
    Task Void();
    [Alias("Int")]
    Task<int> Int();

    static Task<long> StaticLong() => Task.FromResult(0L);
}