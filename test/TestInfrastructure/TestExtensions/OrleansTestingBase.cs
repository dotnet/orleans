using Orleans.Internal;

namespace TestExtensions
{
    public abstract class OrleansTestingBase
    {
        public static long GetRandomGrainId() => ThreadSafeRandom.Next();
    }
}