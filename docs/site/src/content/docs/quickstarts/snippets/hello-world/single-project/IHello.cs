using Orleans;

namespace HelloWorld;

public interface IHello : IGrainWithStringKey
{
    ValueTask<string> SayHello(string greeting);
}
