
namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleObservable, IGrainLifecycle
    {
        public GrainLifecycle(Logger logger) : base(logger)
        {
        }
    }
}
