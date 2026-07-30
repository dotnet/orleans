namespace Tests;

public interface IHelloGrain : IGrainWithGuidKey
{
    ValueTask<string> SayHello(string greeting);
}
