using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;


namespace TestInternalGrainInterfaces
{
    // Note: Self-managed can only implement one grain interface, so have to use copy-paste rather than subclassing 

    internal interface ISimpleActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface ITailCallActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface ILongRunningActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    public interface IBadActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task ThrowSomething();
        Task<long> GetKey();
    }

    internal interface IBadConstructorTestGrain : IGrainWithIntegerKey
    {
        Task<ActivationId> DoSomething();
    }

    internal interface ITaskActionActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface ICreateGrainReferenceTestGrain : IGrainWithIntegerKey
    {
        Task<ActivationId> DoSomething();

        Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain);
    }

    internal interface IActivateDeactivateWatcherGrain : IGrainWithIntegerKey
    {
        Task<ActivationId[]> GetActivateCalls();
        Task<ActivationId[]> GetDeactivateCalls();

        Task Clear();

        Task RecordActivateCall(ActivationId activation);
        Task RecordDeactivateCall(ActivationId activation);
    }
}
