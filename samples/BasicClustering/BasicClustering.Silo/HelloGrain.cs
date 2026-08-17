namespace BasicClustering;

public sealed class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting) =>
        Task.FromResult($"{greeting}. Grain {this.GetPrimaryKeyLong()} is running on {RuntimeIdentity}.");
}
