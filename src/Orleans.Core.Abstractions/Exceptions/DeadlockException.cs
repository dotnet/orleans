using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that a silo has detected a deadlock / loop in a call graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deadlock detection is not enabled by default in Orleans silos, 
    /// because it introduces some extra overhead in call handling.
    /// </para>
    /// <para>
    /// There are some constraints on the types of deadlock that can currently be detected 
    /// by Orleans silos.
    /// </para>
    /// </remarks>
    [Serializable]
    public class DeadlockException : OrleansException
    {
        internal IEnumerable<Tuple<GrainId, string>> CallChain { get; private set; }

        public DeadlockException() : base("Deadlock between grain calls") {}

        public DeadlockException(string message) : base(message) { }

        public DeadlockException(string message, Exception innerException) : base(message, innerException) { }

        internal DeadlockException(string message, IList<Tuple<GrainId, string>> callChain)
            : base(message)
        {
            CallChain = callChain;
        }

        protected DeadlockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            if (info != null)
            {
                CallChain = (IEnumerable<Tuple<GrainId, string>>)info.GetValue("CallChain", typeof(IEnumerable<Tuple<GrainId, string>>));
            }
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info != null)
            {
                info.AddValue("CallChain", this.CallChain, typeof(IEnumerable<Tuple<GrainId, string>>));
            }

            base.GetObjectData(info, context);
        }
    }
}

