using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that a <see cref="GrainReference"/> was not bound to the runtime before being used.
    /// </summary>
    [Serializable]
    public class GrainReferenceNotBoundException : OrleansException
    {
        internal GrainReferenceNotBoundException(GrainReference grainReference) : base(CreateMessage(grainReference)) { }

        private static string CreateMessage(GrainReference grainReference)
        {
            return $"Attempted to use a GrainReference which has not been bound to the runtime: {grainReference.ToString()}." +
                   $" Use the {nameof(IGrainFactory)}.{nameof(IGrainFactory.BindGrainReference)} method to bind this reference to the runtime.";
        }

        internal GrainReferenceNotBoundException(string msg) : base(msg) { }
        internal GrainReferenceNotBoundException(string message, Exception innerException) : base(message, innerException) { }

        protected GrainReferenceNotBoundException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
