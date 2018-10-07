using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace UnitTests.Grains
{
    internal class StreamIdTestGrain : Grain, IStreamIdTestGrain
    {
        public Task<StreamId> GetStreamId(Guid guid, string streamNamespace, string providerName)
        {
            return Task.FromResult(StreamId.GetStreamId(guid, providerName, streamNamespace));
        }
    }
}
