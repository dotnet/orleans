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
        /// <summary>
        /// Initializes a new instance of the <see cref="DeadlockException"/> class.
        /// </summary>
        public DeadlockException()
            : base("Deadlock between grain calls")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeadlockException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public DeadlockException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeadlockException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public DeadlockException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeadlockException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="callChain">
        /// The call chain.
        /// </param>
        internal DeadlockException(string message, IList<GrainId> callChain)
            : base(message)
        {
            CallChain = callChain;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeadlockException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        protected DeadlockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            if (info != null)
            {
                CallChain = (IEnumerable<GrainId>)info.GetValue("CallChain", typeof(IEnumerable<GrainId>));
            }
        }

        /// <summary>
        /// Gets the call chain.
        /// </summary>
        [Id(0)]
        internal IEnumerable<GrainId> CallChain { get; private set; }

        /// <inheritdoc />
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("CallChain", this.CallChain, typeof(IEnumerable<GrainId>));
            base.GetObjectData(info, context);
        }
    }
}

