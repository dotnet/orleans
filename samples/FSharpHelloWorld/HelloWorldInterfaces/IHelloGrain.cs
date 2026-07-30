namespace HelloWorldInterfaces;

public interface IHelloGrain : IGrainWithIntegerKey
{
    ValueTask<string> SayHello(string greeting);
}
