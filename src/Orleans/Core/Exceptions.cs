using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Orleans.Core;

namespace Orleans.Runtime
{
    /// <summary>
    /// An exception class used by the Orleans runtime for reporting errors.
    /// </summary>
    /// <remarks>
    /// This is also the base class for any more specific exceptions 
    /// raised by the Orleans runtime.
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1058:TypesShouldNotExtendCertainBaseTypes")]
    public class OrleansException : Exception
    {
        public OrleansException() : base("Unexpected error.") { }

        public OrleansException(string message) : base(message) { }

        public OrleansException(string message, Exception innerException) : base(message, innerException) { }
#if !NETSTANDARD
        protected OrleansException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }

    /// <summary>
    /// Signifies that a gateway silo is currently in overloaded / load shedding state 
    /// and is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is usaully a transient condition.
    /// The message is likely to be accepted by this or another gateway if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class GatewayTooBusyException : OrleansException
    {
        public GatewayTooBusyException() : base("Gateway too busy") { }

        public GatewayTooBusyException(string message) : base(message) { }

        public GatewayTooBusyException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected GatewayTooBusyException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }

    /// <summary>
    /// Signifies that a silo is in an overloaded state where some 
    /// runtime limit setting is currently being exceeded, 
    /// and so that silo is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is often a transient condition.
    /// The message is likely to be accepted by this or another silo if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class LimitExceededException : OrleansException
    {
        public LimitExceededException() : base("Limit exceeded") { }

        public LimitExceededException(string message) : base(message) { }

        public LimitExceededException(string message, Exception innerException) : base(message, innerException) { }

        public LimitExceededException(string limitName, int current, int threshold, object extraInfo) 
            : base(string.Format("Limit exceeded {0} Current={1} Threshold={2} {3}", limitName, current, threshold, extraInfo)) { }

#if !NETSTANDARD
        protected LimitExceededException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }

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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class DeadlockException : OrleansException
    {
        internal IEnumerable<Tuple<GrainId, string>> CallChain { get; private set; }

        public DeadlockException() : base("Deadlock between grain calls") {}

        public DeadlockException(string message) : base(message) { }

        public DeadlockException(string message, Exception innerException) : base(message, innerException) { }

        internal DeadlockException(List<RequestInvocationHistory> callChain)
            : base(String.Format("Deadlock Exception for grain call chain {0}.", Utils.EnumerableToString(callChain,
                        elem => String.Format("{0}.{1}", elem.GrainId, elem.DebugContext))))
        {
            CallChain = callChain.Select(req => new Tuple<GrainId, string>(req.GrainId, req.DebugContext)).ToList();
        }

#if !NETSTANDARD
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
#endif
    }

    /// <summary>
    /// Signifies that an attempt was made to invoke a grain extension method on a grain where that extension was not installed.
    /// </summary>
    [Serializable]
    public class GrainExtensionNotInstalledException : OrleansException
    {
        public GrainExtensionNotInstalledException() : base("GrainExtensionNotInstalledException") { }
        public GrainExtensionNotInstalledException(string msg) : base(msg) { }
        public GrainExtensionNotInstalledException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected GrainExtensionNotInstalledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }

    /// <summary>
    /// Signifies that an request was cancelled due to target silo unavailability.
    /// </summary>
    [Serializable]
    public class SiloUnavailableException : OrleansException
    {
        public SiloUnavailableException() : base("SiloUnavailableException") { }
        public SiloUnavailableException(string msg) : base(msg) { }
        public SiloUnavailableException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected SiloUnavailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }

    /// <summary>
    /// Signifies that an operation was attempted on an invalid SchedulingContext.
    /// </summary>
    [Serializable]
    internal class InvalidSchedulingContextException : OrleansException
    {
        public InvalidSchedulingContextException() : base("InvalidSchedulingContextException") { }
        public InvalidSchedulingContextException(string msg) : base(msg) { }
        public InvalidSchedulingContextException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected InvalidSchedulingContextException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }

    /// <summary>
    /// Indicates that a client is not longer reachable.
    /// </summary>
    [Serializable]
    public class ClientNotAvailableException : OrleansException
    {
        internal ClientNotAvailableException(IGrainIdentity clientId) : base("No activation for client " + clientId) { }
        internal ClientNotAvailableException(string msg) : base(msg) { }
        internal ClientNotAvailableException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected ClientNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }
}

