#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureCosmos;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureCosmos;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureCosmos;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureCosmos;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureCosmos;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

public enum AzureCosmosThroughputMode
{
    Manual,
    Autoscale,
    Serverless
}