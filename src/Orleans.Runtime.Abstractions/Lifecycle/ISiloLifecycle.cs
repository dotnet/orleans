
namespace Orleans.Runtime
{
    public interface ISiloLifecycle : ILifecycleObservable
    {
        /// <summary>
        /// The highest lifecycle stage which has completed starting.
        /// </summary>
        int HighestCompletedStage { get; }

        /// <summary>
        /// The lowest lifecycle stage which has completed stopping.
        /// </summary>
        int LowestStoppedStage { get; }
    }
}
