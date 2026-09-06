using System;
using System.Runtime.Serialization;
using System.Security;
using Orleans.Serialization.Codecs;

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
        public SerializationConstructorNotFoundException(Type type) : base(GetMessage(type))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializationConstructorNotFoundException" /> class.
        /// </summary>
        /// <param name="info">The serialization information.</param>
        /// <param name="context">The context.</param>
        [SecurityCritical]
#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        protected SerializationConstructorNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }

        private static string GetMessage(Type type)
        {
            ArgumentNullExceptionPolyfill.ThrowIfNull(type);
            return $"Could not find a suitable serialization constructor on type {type.FullName}";
        }
    }
}
