using System;
using System.Collections.Generic;
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
    [GenerateSerializer]
    public class DeadlockException : OrleansException
    {
        [Id(0)]
        internal IEnumerable<GrainId> CallChain { get; private set; }

        public DeadlockException() : base("Deadlock between grain calls") {}

        public DeadlockException(string message) : base(message) { }

        public DeadlockException(string message, Exception innerException) : base(message, innerException) { }

        internal DeadlockException(string message, IList<GrainId> callChain)
            : base(message)
        {
            CallChain = callChain;
        }

        protected DeadlockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            if (info != null)
            {
                CallChain = (IEnumerable<GrainId>)info.GetValue("CallChain", typeof(IEnumerable<GrainId>));
            }
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info != null)
            {
                info.AddValue("CallChain", this.CallChain, typeof(IEnumerable<GrainId>));
            }

            base.GetObjectData(info, context);
        }
    }
}

