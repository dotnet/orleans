using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;


namespace UnitTestGrainInterfaces
{
    [ExtendedPrimaryKey]
    internal interface IKeyExtensionTestGrain : IGrain
    {
        Task<GrainId> GetGrainId();
        Task<ActivationId> GetActivationId();
    }
}
