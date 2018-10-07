using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    internal interface IStreamIdTestGrain : IGrainWithIntegerKey
    {
        Task<StreamId> GetStreamId(Guid guid, string streamNamespace, string providerName);
    }
}
