using Orleans;

namespace MyNs;

[GenerateSerializer]
public class TestClass
{
    [Id(0)]
    public string A { get; set; } = "";
}

[GenerateSerializer]
public struct TestStruct
{
    public string A;
}

[GenerateSerializer]
public record TestRecord { }

[GenerateSerializer]
public record struct TestRecordStruct { }

[Alias("TestGrain")]
public interface ITestGrain : IGrainWithStringKey
{
    [Alias("Void")]
    Task Void();
}

public class MyClass
{

}