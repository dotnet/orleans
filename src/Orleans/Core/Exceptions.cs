using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Orleans.Core;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that a gateway silo is currently in overloaded / load shedding state 
    /// and is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is usaully a transient condition.
    /// The message is likely to be accepted by this or another gateway if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    public class GatewayTooBusyException : OrleansException
    {
        public GatewayTooBusyException() : base("Gateway too busy") { }

        public GatewayTooBusyException(string message) : base(message) { }

        public GatewayTooBusyException(string message, Exception innerException) : base(message, innerException) { }

        protected GatewayTooBusyException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
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
    public class LimitExceededException : OrleansException
    {
        public LimitExceededException() : base("Limit exceeded") { }

        public LimitExceededException(string message) : base(message) { }

        public LimitExceededException(string message, Exception innerException) : base(message, innerException) { }

        public LimitExceededException(string limitName, int current, int threshold, object extraInfo) 
            : base(string.Format("Limit exceeded {0} Current={1} Threshold={2} {3}", limitName, current, threshold, extraInfo)) { }

        protected LimitExceededException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
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

    /// <summary>
    /// Signifies that an attempt was made to invoke a grain extension method on a grain where that extension was not installed.
    /// </summary>
    [Serializable]
    public class GrainExtensionNotInstalledException : OrleansException
    {
        public GrainExtensionNotInstalledException() : base("GrainExtensionNotInstalledException") { }
        public GrainExtensionNotInstalledException(string msg) : base(msg) { }
        public GrainExtensionNotInstalledException(string message, Exception innerException) : base(message, innerException) { }

        protected GrainExtensionNotInstalledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }

    /// <summary>
    /// Signifies that an request was cancelled due to target silo unavailability.
    /// </summary>
    [Serializable]
    public class SiloUnavailableException : OrleansMessageRejectionException
    {
        public SiloUnavailableException() : base("SiloUnavailableException") { }
        public SiloUnavailableException(string msg) : base(msg) { }
        public SiloUnavailableException(string message, Exception innerException) : base(message, innerException) { }

        protected SiloUnavailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
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

        protected InvalidSchedulingContextException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
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

        protected ClientNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }

    /// <summary>
    /// Indicates that a <see cref="GrainReference"/> was not bound to the runtime before being used.
    /// </summary>
    [Serializable]
    public class GrainReferenceNotBoundException : OrleansException
    {
        internal GrainReferenceNotBoundException(GrainReference grainReference) : base(CreateMessage(grainReference)) { }

        private static string CreateMessage(GrainReference grainReference)
        {
            return $"Attempted to use a GrainReference which has not been bound to the runtime: {grainReference.ToDetailedString()}." +
                   $" Use the {nameof(IGrainFactory)}.{nameof(IGrainFactory.BindGrainReference)} method to bind this reference to the runtime.";
        }

        internal GrainReferenceNotBoundException(string msg) : base(msg) { }
        internal GrainReferenceNotBoundException(string message, Exception innerException) : base(message, innerException) { }

        protected GrainReferenceNotBoundException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }

    /// <summary>
    /// Indicates that an Orleans message was rejected.
    /// </summary>
    [Serializable]
    public class OrleansMessageRejectionException : OrleansException
    {
        internal OrleansMessageRejectionException(string message)
            : base(message)
        {
        }

        internal OrleansMessageRejectionException(string message,
            Exception innerException) : base(message, innerException)
        {
        }

        protected OrleansMessageRejectionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }

    /// <summary>
    /// Indicates a lifecycle was canceled, either by request or due to observer error.
    /// </summary>
    [Serializable]
    public class OrleansLifecycleCanceledException : OrleansException
    {
        internal OrleansLifecycleCanceledException(string message)
            : base(message)
        {
        }

        internal OrleansLifecycleCanceledException(string message,
            Exception innerException) : base(message, innerException)
        {
        }

        protected OrleansLifecycleCanceledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

