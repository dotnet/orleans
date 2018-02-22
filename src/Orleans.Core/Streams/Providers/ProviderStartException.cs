using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Exception thrown whenever a provider has failed to be started.
    /// </summary>
    [Serializable]
    public class ProviderStartException : OrleansException
    {
        public ProviderStartException()
        { }
        public ProviderStartException(string msg)
            : base(msg)
        { }
        public ProviderStartException(string msg, Exception exc)
            : base(msg, exc)
        { }

        protected ProviderStartException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
