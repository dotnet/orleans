namespace OrleansApp.Contracts;

public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string name);
}
