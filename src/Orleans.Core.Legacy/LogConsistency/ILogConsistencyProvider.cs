using Orleans.Providers;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Interface to be implemented for a log consistency provider.
    /// </summary>
    public interface ILogConsistencyProvider : IProvider, ILogViewAdaptorFactory
    {
    }
}
