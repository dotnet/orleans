using System;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Memory
{
    [Serializable]
    public class MemoryEventData
    {
        public Guid StreamGuid { get; private set; }
        public String StreamNamespace { get; private set; }
        public List<object> Events { get; private set; }
        public Dictionary<string, object> RequestContext { get; private set; }
         
        public MemoryEventData(Guid streamGuid, String streamNamespace, List<object> events, Dictionary<string, object> requestContext)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            Events = events;
            RequestContext = requestContext;
        }
    }
}
