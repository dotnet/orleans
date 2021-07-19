using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ISiloRoleBasedPlacementGrain : IGrainWithStringKey
    {
        Task<bool> Ping();
    }
}
