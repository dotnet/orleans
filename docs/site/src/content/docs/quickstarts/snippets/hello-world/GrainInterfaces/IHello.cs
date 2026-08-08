using Orleans;

namespace GrainInterfaces;

// <grain-interface>
public interface IHello : IGrainWithStringKey
{
    ValueTask<string> SayHello(string greeting);
}
// </grain-interface>
