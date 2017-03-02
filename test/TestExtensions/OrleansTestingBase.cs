using System;

namespace TestExtensions
{
    public abstract class OrleansTestingBase
    {
        protected static readonly Random random = new Random();

        protected static long GetRandomGrainId()
        {
            return random.Next();
        }
    }
}