using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that a <see cref="GrainReference"/> was not bound to the runtime before being used.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GrainReferenceNotBoundException : OrleansException
    {
        internal GrainReferenceNotBoundException(GrainReference grainReference) : base(CreateMessage(grainReference)) { }

        private static string CreateMessage(GrainReference grainReference)
        {
            return $"Attempted to use an invalid GrainReference, which has not been constructed by the runtime: {grainReference}.";
        }

        internal GrainReferenceNotBoundException(string msg) : base(msg) { }
        internal GrainReferenceNotBoundException(string message, Exception innerException) : base(message, innerException) { }

        protected GrainReferenceNotBoundException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
