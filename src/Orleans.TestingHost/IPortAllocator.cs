using System;

namespace Orleans.TestingHost
{
    public interface ITestClusterPortAllocator : IDisposable
    {
        ValueTuple<int, int> AllocateConsecutivePortPairs(int numPorts);
    }
}