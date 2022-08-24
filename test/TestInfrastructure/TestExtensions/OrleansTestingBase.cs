using System;
using Orleans.Internal;

namespace TestExtensions
{
    public abstract class OrleansTestingBase
    {
        public static long GetRandomGrainId() => Random.Shared.Next();
    }
}