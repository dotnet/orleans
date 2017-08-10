
namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleObservable<GrainLifecyleStage>, IGrainLifecycle
    {
        public GrainLifecycle(Logger logger) : base(logger)
        {
        }
    }
}
