using System;
using System.Threading.Tasks;
using Orleans.Storage;

namespace Orleans.Runtime
{
    //TODO: this interface should not meta-data for a grain activation. 
    internal interface IActivationData
    {
        GrainReference GrainReference { get; }
        GrainId Identity { get; }
        string GrainTypeName { get; }
        Grain GrainInstance { get; }
        ActivationId ActivationId { get; }
        ActivationAddress Address { get; }
        void DelayDeactivation(TimeSpan timeSpan);
        IStorageProvider StorageProvider { get; }
        IGrainTimer RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);
        void OnTimerDisposed(IGrainTimer timer);
    }
}
