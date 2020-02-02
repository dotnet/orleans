using Orleans;
using System.Threading.Tasks;

namespace OneBoxDeployment.GrainInterfaces
{
    /// <summary>
    /// A grain for testing grain state.
    /// </summary>
    public interface ITestStateGrain: IGrainWithIntegerKey
    {
        /// <summary>
        /// Increments the current state by <paramref name="incrementBy"/>.
        /// </summary>
        /// <param name="incrementBy">The amount to increment current state.</param>
        /// <returns>The new, incremented state.</returns>
        Task<int> Increment(int incrementBy);
    }
}
