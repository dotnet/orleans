using System;
using System.Runtime.Serialization;
using System.Security;

namespace Orleans.Serialization.ISerializableSupport
{
    [Serializable]
    public class SerializationConstructorNotFoundException : Exception
    {
        [SecurityCritical]
        public SerializationConstructorNotFoundException(Type type) : base(
            (string)$"Could not find a suitable serialization constructor on type {type.FullName}")
        {
        }

        [SecurityCritical]
        protected SerializationConstructorNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}