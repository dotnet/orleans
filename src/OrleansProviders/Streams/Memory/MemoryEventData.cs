using System;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Memory
{
    /// <summary>
    /// Represents the event sent and received from an In-Memory queue grain. 
    /// </summary>
    [Serializable]
    public class MemoryEventData
    {
        /// <summary>
        /// Stream Guid of the event data.
        /// </summary>
        public Guid StreamGuid { get; private set; }

        /// <summary>
        /// Stream namespace of the event data.
        /// </summary>
        public String StreamNamespace { get; private set; }

        /// <summary>
        /// List of event body.
        /// </summary>
        public List<object> Events { get; private set; }

        /// <summary>
        /// Request context.
        /// </summary>
        public Dictionary<string, object> RequestContext { get; private set; }

        /// <summary>
        /// Constructor that initializes the data
        /// </summary>
        public MemoryEventData(Guid streamGuid, String streamNamespace, List<object> events, Dictionary<string, object> requestContext)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            Events = events;
            RequestContext = requestContext;
        }
    }
}
