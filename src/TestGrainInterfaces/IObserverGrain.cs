using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using System.Collections;

namespace UnitTests.GrainInterfaces
{
    public interface IObserverGrain : IGrainWithIntegerKey
    {
        Task SetTarget(ISimpleObserverableGrain target);
        Task Subscribe(ISimpleGrainObserver observer);
    }
}
