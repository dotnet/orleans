
namespace MyNs;

[Orleans.GenerateSerializer]
public class Test
{
    [Id] public string A { get; set; } = "";
}

[AttributeUsage(AttributeTargets.All)]
public class IdAttribute : Attribute
{

}

/*
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
*/

