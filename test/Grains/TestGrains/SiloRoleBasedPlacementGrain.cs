using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    [SiloRoleBasedPlacement]
    public class SiloRoleBasedPlacementGrain : Grain, ISiloRoleBasedPlacementGrain
    {
        public Task<bool> Ping()
        {
            return Task.FromResult(true);
        }
    }
}
