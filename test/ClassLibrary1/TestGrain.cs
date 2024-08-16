namespace ClassLibrary1;

public interface ITestGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class TestGrain : Grain, ITestGrain
{
    public Task Ping() => Task.CompletedTask;
}