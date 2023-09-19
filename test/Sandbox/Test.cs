using Orleans;
using Orleans.Runtime;

namespace TestNS;

[Serializable]
[GenerateSerializer]
public class Test
{
    public string A { get; set; } = "";
}


[Alias("TestGrain")]
public interface ITestGrain : IGrainWithStringKey
{
//    [Alias("Void")]
    Task Void();
    static Task<int> Static() => Task.FromResult(0);
}

public interface ITest
{
    Task Void();
}