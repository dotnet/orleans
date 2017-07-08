using System;

namespace Orleans
{
    public interface ILifecycleObservable<in TRing>
    {
        IDisposable Subscribe(TRing ring, ILifecycleObserver observer);
    }
}
