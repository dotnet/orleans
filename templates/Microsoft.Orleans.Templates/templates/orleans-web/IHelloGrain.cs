namespace OrleansWebApp;

public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string name);
}
