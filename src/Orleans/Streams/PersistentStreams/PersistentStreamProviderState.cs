using System;

namespace Orleans.Providers.Streams.Common
{
    [Serializable]
    public enum PersistentStreamProviderState
    {
        None,
        Initialized,
        AgentsStarted,
        AgentsStopped,
    }
}