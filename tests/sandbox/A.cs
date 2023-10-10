

using Orleans;

namespace A
{
    [Alias("IMyGrain")]
    public interface IMyGrain : IGrainWithStringKey
    {

    }

    [Alias("IMyGrain")]
    public interface IMyGrain1 : IGrainWithStringKey
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