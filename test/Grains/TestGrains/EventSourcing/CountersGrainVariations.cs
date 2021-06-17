using Orleans.Concurrency;
using Orleans.Providers;

namespace TestGrains
{
    // we define four variants of the CountersGrain that have different configurations.
    //
    // all variants use the SlowMemoryStore storage provider; it delays all storage accesses by 10ms
    // to simulate cloud storage latency
    //
    // the other configurations pick all four combinations of:
    // state vs. log storage
    // reentrant vs. non-reentrant

    /// When using the StateStorage consistency provider, we persist the latest state only ... we are 
    /// not truly "event sourcing", as we do not want to persist 
    /// the events, but only state snapshots.
    /// 
    /// When using the LogStorage consistency provider, we persist the log,
    /// i.e. the complete sequence of all events

    [LogConsistencyProvider(ProviderName = "StateStorage")]
    [StorageProvider(ProviderName = "SlowMemoryStore")]
    public class CountersGrain_StateStore_NonReentrant : CountersGrain
    {
    }

    [LogConsistencyProvider(ProviderName = "StateStorage")]
    [StorageProvider(ProviderName = "SlowMemoryStore")]
    [Reentrant]
    public class CountersGrain_StateStore_Reentrant : CountersGrain
    {
    }
    
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    [StorageProvider(ProviderName = "SlowMemoryStore")]
    public class CountersGrain_LogStore_NonReentrant : CountersGrain
    {
    }

    [LogConsistencyProvider(ProviderName = "LogStorage")]
    [StorageProvider(ProviderName = "SlowMemoryStore")]
    [Reentrant]
    public class CountersGrain_LogStore_Reentrant : CountersGrain
    {
    }

 


}
