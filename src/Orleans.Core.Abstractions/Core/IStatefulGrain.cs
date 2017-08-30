using Orleans.Core;

namespace Orleans
{
    internal interface IStatefulGrain
    {
        IGrainState GrainState { get; }

        void SetStorage(IStorage storage);
    }
}
