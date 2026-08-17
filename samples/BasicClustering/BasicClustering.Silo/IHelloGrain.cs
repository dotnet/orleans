namespace BasicClustering;

public interface IHelloGrain : IGrainWithIntegerKey
{
    Task<string> SayHello(string greeting);
}
