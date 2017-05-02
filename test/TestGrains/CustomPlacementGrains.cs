﻿using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public abstract class CustomPlacementBaseGrain : Grain, ICustomPlacementTestGrain
    {
        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }
    }

    [TestPlacementStrategy(CustomPlacementScenario.FixedSilo)]
    public class CustomPlacement_FixedSiloGrain : CustomPlacementBaseGrain
    {
    }

    [TestPlacementStrategy(CustomPlacementScenario.ExcludeOne)]
    public class CustomPlacement_ExcludeOneGrain : CustomPlacementBaseGrain
    {
    }
}
