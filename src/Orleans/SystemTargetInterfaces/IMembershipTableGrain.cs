using Orleans.Concurrency;

namespace Orleans
{
    /// <summary>
    /// Membership table interface for grain based implementation.
    /// </summary>
    [Unordered]
    public interface IMembershipTableGrain : IGrainWithGuidKey, IMembershipTable
    {
        
    }
}