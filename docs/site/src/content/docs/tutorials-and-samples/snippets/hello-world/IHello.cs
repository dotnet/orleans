// <hello_world_grain_interface>
using Orleans;

namespace HelloWorld;

public interface IHello : IGrainWithStringKey
{
    ValueTask<string> SayHello(string greeting);
}
// </hello_world_grain_interface>
