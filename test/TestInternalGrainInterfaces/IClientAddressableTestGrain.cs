﻿using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    [Factory(FactoryAttribute.FactoryTypes.Grain)]
    public interface IClientAddressableTestGrain : IGrainWithIntegerKey
    {
        Task SetTarget(IClientAddressableTestClientObject target);
        Task<string> HappyPath(string message);
        Task SadPath(string message);
        Task MicroSerialStressTest(int iterationCount);
        Task MicroParallelStressTest(int iterationCount);
    }
}
