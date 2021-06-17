using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Providers
{
    /// <summary>
    /// Exception thrown whenever a provider has failed to be initialized.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class ProviderInitializationException : OrleansException
    {
        public ProviderInitializationException()
        { }
        public ProviderInitializationException(string msg)
            : base(msg)
        { }
        public ProviderInitializationException(string msg, Exception exc)
            : base(msg, exc)
        { }

        protected ProviderInitializationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
