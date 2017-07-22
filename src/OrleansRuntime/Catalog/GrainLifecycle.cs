
namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleObservable<GrainLifecyleStage>, IGrainLifeCycle
    {
        public GrainLifecycle(Logger logger) : base(logger)
        {
        }
    }
}
