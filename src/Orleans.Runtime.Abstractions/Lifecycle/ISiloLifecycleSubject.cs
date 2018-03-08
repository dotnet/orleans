
namespace Orleans.Runtime
{
    /// <summary>
    /// Observable silo lifecycle and observer.
    /// </summary>
    public interface ISiloLifecycleSubject : ISiloLifecycle, ILifecycleObserver
    {
    }
}
