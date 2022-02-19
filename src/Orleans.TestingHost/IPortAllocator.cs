using System;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Functionality for finding unused ports.
    /// </summary>
    public interface ITestClusterPortAllocator : IDisposable
    {
        /// <summary>
        /// Allocates consecutive port pairs.
        /// </summary>
        /// <param name="numPorts">The number of consecutive ports to allocate.</param>
        /// <returns>Base ports for silo and gateway endpoints.</returns>
        ValueTuple<int, int> AllocateConsecutivePortPairs(int numPorts);
    }
}