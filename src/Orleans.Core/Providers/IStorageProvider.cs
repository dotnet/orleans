using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Exception thrown whenever a grain call is attempted with a bad / missing storage provider configuration settings for that grain.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class BadProviderConfigException : OrleansException
    {
        public BadProviderConfigException()
        { }
        public BadProviderConfigException(string msg)
            : base(msg)
        { }
        public BadProviderConfigException(string msg, Exception exc)
            : base(msg, exc)
        { }

        protected BadProviderConfigException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
