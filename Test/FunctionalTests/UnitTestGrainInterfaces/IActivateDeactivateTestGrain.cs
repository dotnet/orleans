using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;


namespace UnitTestGrainInterfaces
{
    // Note: Self-managed can only implement one grain interface, so have to use copy-paste rather than subclassing 

    internal interface ISimpleActivateDeactivateTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface ITailCallActivateDeactivateTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface ILongRunningActivateDeactivateTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    public interface IBadActivateDeactivateTestGrain : IGrain
    {
        Task ThrowSomething();
        Task<long> GetKey();
    }

    internal interface IBadConstructorTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
    }

    internal interface ITaskActionActivateDeactivateTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface ICreateGrainReferenceTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();

        Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain);
    }

    internal interface IActivateDeactivateWatcherGrain : IGrain
    {
        Task<ActivationId[]> GetActivateCalls();
        Task<ActivationId[]> GetDeactivateCalls();

        Task Clear();

        Task RecordActivateCall(ActivationId activation);
        Task RecordDeactivateCall(ActivationId activation);
    }
}
