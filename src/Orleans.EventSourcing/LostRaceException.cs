using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Exception thrown whenever an event loses a race, i.e. if, after an event
    /// is raised and before it is appended to the shared log, some other event is appended first.
    /// </summary>
    [Serializable]
    public class LostRaceException : OrleansException
    {
        public LostRaceException()
        { }

        public LostRaceException(string msg)
            : base(msg)
        { }
 
        protected LostRaceException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
