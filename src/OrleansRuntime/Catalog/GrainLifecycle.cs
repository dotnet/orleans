
namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleObservable<GrainLifecycleStage>, IGrainLifecycle
    {
        public GrainLifecycle(Logger logger) : base(logger)
        {
        }
    }
}
