using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Providers
{
    [Serializable]
    public class ProviderStateException : OrleansException
    {
        public ProviderStateException() : base("Unexpected provider state")
        { }
        public ProviderStateException(string message) : base(message) { }

        public ProviderStateException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected ProviderStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }
}