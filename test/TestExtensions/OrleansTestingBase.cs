using System;
using Orleans;
using Orleans.Runtime;

namespace TestExtensions
{
    public abstract class OrleansTestingBase
    {
        protected static readonly Random random = new Random();

        public Logger logger => GrainClient.Logger;

        protected static IGrainFactory GrainFactory => GrainClient.GrainFactory;

        protected static long GetRandomGrainId()
        {
            return random.Next();
        }
    }
}