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
        IServiceProvider ServiceProvider { get; }
        void DelayDeactivation(TimeSpan timeSpan);
        IStorageProvider StorageProvider { get; }
        void OnTimerCreated(IGrainTimer timer);
        void OnTimerDisposed(IGrainTimer timer);
    }
}
