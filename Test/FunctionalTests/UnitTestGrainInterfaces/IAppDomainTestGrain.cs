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

    internal interface IAppDomainTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }

    internal interface IAppDomainHostTestGrain : IGrain
    {
        Task<ActivationId> DoSomething();
        Task DoDeactivate();
    }
}
