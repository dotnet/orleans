

using Orleans;

namespace A
{
    [Alias("IMyGrain")]
    public interface IMyGrain : IGrainWithStringKey
    {

    }
}

namespace B
{
    [Alias("IMyGrain")]
    public interface IMyGrain : IGrainWithStringKey
    {

    }
}