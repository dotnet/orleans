using System;
using System.Runtime.Serialization;
using System.Security;

namespace Orleans.Serialization
{
    /// <summary>
    /// Thrown when a type has no serialization constructor.
    /// </summary>
    [Serializable]
    public class SerializationConstructorNotFoundException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SerializationConstructorNotFoundException"/> class.
        /// </summary>
        /// <param name="type">The type.</param>
        [SecurityCritical]
        public SerializationConstructorNotFoundException(Type type) : base(
            (string)$"Could not find a suitable serialization constructor on type {type.FullName}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializationConstructorNotFoundException" /> class.
        /// </summary>
        /// <param name="info">The serialization information.</param>
        /// <param name="context">The context.</param>
        [SecurityCritical]
        protected SerializationConstructorNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}