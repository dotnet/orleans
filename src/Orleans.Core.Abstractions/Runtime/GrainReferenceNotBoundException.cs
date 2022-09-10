using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that a <see cref="GrainReference"/> was not bound to the runtime before being used.
    /// </summary>
    [Serializable, GenerateSerializer]
    public sealed class GrainReferenceNotBoundException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceNotBoundException"/> class.
        /// </summary>
        /// <param name="grainReference">The unbound grain reference.</param>
        internal GrainReferenceNotBoundException(GrainReference grainReference) : base(CreateMessage(grainReference)) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceNotBoundException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        internal GrainReferenceNotBoundException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceNotBoundException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        internal GrainReferenceNotBoundException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceNotBoundException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private GrainReferenceNotBoundException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        private static string CreateMessage(GrainReference grainReference)
        {
            return $"Attempted to use an invalid GrainReference, which has not been constructed by the runtime: {grainReference}.";
        }
    }
}
