using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Distributed.GrainInterfaces.Streaming
{
    public static class Constants
    {
        public const string StreamingProvider = "TestStreamingProvider";
        public const string StreamingNamespace = "TestStreamingNamespace";

        public const string DefaultCounterGrain = "default";
    }
}
